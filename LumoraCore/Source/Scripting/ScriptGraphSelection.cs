// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The editing operations on many nodes at once, the way a hand selects them: a node's whole wired set, and
// nodes deleted, copied or packed together, each one undo step for whoever does it. -xlinka
public static partial class ScriptGraphEditing
{
    public static readonly LocaleText DeleteStep = LocaleText.Keyed("Loom.Undo.Delete", "Delete Nodes");

    // A node and every node wired to it, either way and however far, within its graph. A Write Value pointing at
    // its Stored Value counts as wired to it, since that joined their groups the same way.
    public static List<ScriptGraphNode> ConnectedSet(ScriptGraphNode node)
    {
        var set = new List<ScriptGraphNode>();
        if (node == null || node.IsDestroyed)
            return set;
        set.Add(node);
        if (node.Space is not { } space)
            return set;

        var links = new Dictionary<ScriptGraphNode, List<ScriptGraphNode>>();
        void Link(ScriptGraphNode from, ScriptGraphNode? to)
        {
            if (to == null || to.IsDestroyed || ReferenceEquals(from, to) || !ReferenceEquals(to.Space, space))
                return;
            if (!links.TryGetValue(from, out var outward))
                links[from] = outward = new List<ScriptGraphNode>();
            outward.Add(to);
            if (!links.TryGetValue(to, out var inward))
                links[to] = inward = new List<ScriptGraphNode>();
            inward.Add(from);
        }
        foreach (var each in space.Nodes)
        {
            foreach (var wire in each.Inputs)
                Link(each, wire.Node.Target);
            foreach (var wire in each.Exits)
                Link(each, wire.Node.Target);
            foreach (var value in each.Values)
                Link(each, value.Reference.Target as ScriptGraphNode);
        }

        var seen = new HashSet<ScriptGraphNode> { node };
        for (int i = 0; i < set.Count; i++)
        {
            if (!links.TryGetValue(set[i], out var next))
                continue;
            foreach (var other in next)
            {
                if (seen.Add(other))
                    set.Add(other);
            }
        }
        return set;
    }

    // Nodes deleted together as one undo step. A group standing on its own that loses every node it has goes with
    // them, as it goes with its last node deleted alone, and undo brings both back. A node in this machine's hand
    // is let go of first; one in someone else's hand stays, and so does one whoever deletes may not remove. The
    // reason says how many stayed. How many went.
    public static int DeleteNodes(IEnumerable<ScriptGraphNode> nodes, out string? error)
    {
        error = null;
        var doomed = new HashSet<ScriptGraphNode>();
        World? world = null;
        int held = 0;
        foreach (var node in nodes)
        {
            if (node is not { IsDestroyed: false, World: { } at } || doomed.Contains(node))
                continue;
            world ??= at;
            // Let go of before it goes, or the hand would put it back where it came from when it opens.
            if (node.Slot.GetComponent<Grabbable>() is { IsGrabbed: true } grab)
            {
                if (grab.Grabber is not { } hand || !ReferenceEquals(hand.OwningUser, at.LocalUser))
                {
                    held++;
                    continue;
                }
                hand.Release(grab);
            }
            doomed.Add(node);
        }
        if (world == null)
        {
            error = "those nodes are gone";
            return 0;
        }

        var gone = new List<(Slot Slot, int Nodes)>();
        var groups = new HashSet<Slot>();
        foreach (var node in doomed)
        {
            if (node.Space is { } space && IsImplicit(space) && space.Slot?.Parent is { IsRootSlot: false } group
                && space.Nodes.All(doomed.Contains))
            {
                if (groups.Add(group))
                    gone.Add((group, space.Nodes.Count()));
                continue;
            }
            gone.Add((node.Slot, 1));
        }

        int deleted = 0, refused = 0;
        var undo = FindUndo(world);
        using (undo != null ? undo.BeginBatch(DeleteStep) : default)
        {
            foreach (var (slot, count) in gone)
            {
                try
                {
                    if (slot.GetComponent<ScriptGraphNode>() is { } node)
                    {
                        if (DeleteNode(node, out _))
                            deleted += count;
                        else
                            refused += count;
                        continue;
                    }
                    if (undo != null && SlotExistenceUndoBatch.Destroy(world, new[] { slot }) is { } removed)
                        undo.Record(removed);
                    else
                        slot.Destroy();
                    deleted += count;
                }
                catch (UnauthorizedAccessException)
                {
                    refused += count;
                }
            }
        }
        if (refused > 0)
            error = refused == 1 ? "one node is not yours to delete" : $"{refused} nodes are not yours to delete";
        else if (held > 0)
            error = held == 1 ? "one node is in someone else's hand" : $"{held} nodes are in someone else's hands";
        return deleted;
    }

    // Copies of nodes, each in its own node's graph, made in one pass: a wire or a pointer between two of them
    // joins the two copies, and one to a node left out stays on that node, the way a copied slot keeps what it
    // points at outside itself. Each copy lies on its node as it is now, a node in a hand included, with an id of
    // its own, and a shader copy that makes a material property with a name of its own.
    // Not recorded: the copies are for the caller's undo step.
    public static List<Slot> CopyNodes(IEnumerable<ScriptGraphNode> nodes)
    {
        var sources = new List<Slot>();
        var graphs = new List<Slot>();
        foreach (var node in nodes)
        {
            if (node is { IsDestroyed: false } && node.Space?.Slot is { IsDestroyed: false } graph && !sources.Contains(node.Slot))
            {
                sources.Add(node.Slot);
                graphs.Add(graph);
            }
        }
        var copies = new List<Slot>();
        if (sources.Count == 0)
            return copies;

        var before = new Dictionary<Slot, HashSet<Slot>>();
        foreach (var graph in graphs)
            before.TryAdd(graph, new HashSet<Slot>(graph.Children));
        List<Slot?> made;
        try
        {
            made = Slot.DuplicateTogether(sources, graphs, preserveGlobalTransform: true);
        }
        catch (UnauthorizedAccessException)
        {
            // A refused copy can leave part of one behind; whatever came is taken back.
            foreach (var (graph, had) in before)
            {
                foreach (var child in graph.Children.ToList())
                {
                    if (!had.Contains(child))
                    {
                        try
                        {
                            child.Destroy();
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }
            }
            return copies;
        }

        var named = new Dictionary<ScriptGraphSpace, List<ScriptGraphNode>>();
        foreach (var copy in made)
        {
            if (copy == null)
                continue;
            if (copy.GetComponent<ScriptGraphNode>() is { } node && node.Space is { Kind.Value: GraphKind.Shader } space)
            {
                if (!named.TryGetValue(space, out var list))
                    named[space] = list = new List<ScriptGraphNode>();
                list.Add(node);
            }
            copies.Add(copy);
        }
        // Undoing the copies takes them away, names and all, so the renames need no step of their own.
        foreach (var (space, renamed) in named)
            EnsureUniquePropertyNames(space, renamed);
        return copies;
    }

    // Every group standing on its own among the given packed into an object picked by its card: the object's script
    // takes them all in, beside what it already holds, as one undo step, and is packed as another, the way one group
    // is. A shader graph, a script of its own and a group the object is part of are passed over.
    public static Task<string?> PackIntoAsync(IEnumerable<ScriptGraphSpace> groups, Slot target)
    {
        if (target == null || target.IsDestroyed || target.IsRootSlot || target.World is not { } world)
            return Task.FromResult<string?>("pick an object to pack into");
        var loose = new List<ScriptGraphSpace>();
        foreach (var group in groups)
        {
            if (group is { IsDestroyed: false } && group.Kind.Value == GraphKind.Script && IsImplicit(group) && !loose.Contains(group)
                && group.Slot?.Parent is { } slot && !ReferenceEquals(target, slot) && !target.IsDescendantOf(slot))
                loose.Add(group);
        }
        if (loose.Count == 0)
            return Task.FromResult<string?>("there are no nodes standing on their own to pack");

        ScriptGraphSpace? into;
        string? refused = null;
        int taken = 0;
        var undo = FindUndo(world);
        using (undo != null ? undo.BeginBatch(JoinStep) : default)
        {
            into = EditScriptOn(target, out var error);
            if (into == null)
                return Task.FromResult<string?>(error ?? "that object's script could not be opened");
            var already = new HashSet<ScriptGraphNode>(into.Nodes);
            foreach (var group in loose)
            {
                var merge = ScriptMergeUndo.Take(into, group, out error);
                if (merge == null)
                {
                    refused ??= error;
                    continue;
                }
                InspectorUndo.Record(world, merge);
                taken++;
            }
            Beside(into, already);
        }
        if (taken == 0)
            return Task.FromResult(refused);
        var pack = PackAsync(into);
        return refused == null ? pack : pack.ContinueWith<string?>(done => done.Result ?? refused, TaskScheduler.Default);
    }

    // The scripts and groups some nodes are in, each packed where it stands, all as one undo step. A shader graph
    // never packs: its nodes are passed over, and the answer says so. The first refusal, or null.
    public static async Task<string?> PackGraphsOf(IEnumerable<ScriptGraphNode> nodes)
    {
        var spaces = new List<ScriptGraphSpace>();
        int shader = 0;
        foreach (var node in nodes)
        {
            if (node is not { IsDestroyed: false } || node.Space is not { IsDestroyed: false } space)
                continue;
            if (space.Kind.Value == GraphKind.Shader)
                shader++;
            else if (!spaces.Contains(space))
                spaces.Add(space);
        }
        // Started here, on the world's thread, which is where reading a graph out has to happen.
        var error = spaces.Count == 0 ? null : await PackTogetherAsync(spaces).ConfigureAwait(false);
        if (shader > 0)
            error ??= shader == 1 ? "a shader node never packs, so it was left as it is" : $"shader nodes never pack, so {shader} were left as they are";
        return error;
    }
}
