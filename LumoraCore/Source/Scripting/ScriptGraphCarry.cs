// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Components;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Core.Math;

namespace Lumora.Core.Scripting;

// A node carried by hand and let go of stays where the hand left it, at whatever depth and turn, the way any
// object does; nothing pulls it back onto its graph's plane. Only the machine whose hand picked it up records
// the move, and everything let go of at once is one undo step back to where each node was picked up. -xlinka
public static partial class ScriptGraphEditing
{
    public static readonly LocaleText MoveStep = LocaleText.Keyed("Loom.Undo.Move", "Move Nodes");

    private readonly record struct Pickup(Slot Graph, float3 Position, floatQ Rotation, float3 Scale);

    private sealed class Carries
    {
        public readonly Dictionary<ScriptGraphNode, Pickup> Held = new();
        public readonly List<(ScriptGraphNode Node, Slot Graph, List<IUndoBatch> Records)> Landed = new();
        public bool Queued;
    }

    private static readonly ConditionalWeakTable<World, Carries> CarriesIn = new();

    // A hand here took hold of a node: where it sat in its graph, kept for the undo step of its landing.
    internal static void PickedUp(ScriptGraphNode node, Grabbable grab)
    {
        var world = node.World;
        if (world == null || node.IsDestroyed || node.Slot is not { } slot || grab.LastParentRef.Target is not { IsDestroyed: false } graph
            || graph.GetComponent<ScriptGraphSpace>() == null)
            return;
        var carries = CarriesIn.GetValue(world, static _ => new Carries());
        // A node destroyed in a hand is never let go of.
        List<ScriptGraphNode>? gone = null;
        foreach (var held in carries.Held.Keys)
        {
            if (held.IsDestroyed)
                (gone ??= new List<ScriptGraphNode>()).Add(held);
        }
        if (gone != null)
        {
            foreach (var held in gone)
                carries.Held.Remove(held);
        }
        // Worked back out through the hand, so rounding is taken off: a node that sat level, upright and at its own
        // size is kept exactly so.
        var position = graph.GlobalPointToLocal(slot.GlobalPosition);
        if (MathF.Abs(position.z) < Rounding)
            position.z = 0f;
        var rotation = graph.GlobalRotationToLocal(slot.GlobalRotation);
        if (MathF.Abs(floatQ.Dot(rotation, floatQ.Identity)) > 1f - Rounding)
            rotation = floatQ.Identity;
        var scale = graph.GlobalScaleToLocal(slot.GlobalScale);
        if ((scale - float3.One).Length < Rounding)
            scale = float3.One;
        carries.Held[node] = new Pickup(graph, position, rotation, scale);
        grab.SuppressMoveUndo();
    }

    // Below this a pose read back through a hand is the same pose.
    private const float Rounding = 1e-5f;

    // A hand here let go of a node it picked up: into the undo step of this release unless it went back where it
    // was.
    internal static void LetGo(ScriptGraphNode node, Grabbable grab)
    {
        var world = node.World;
        if (world == null || !CarriesIn.TryGetValue(world, out var carries) || !carries.Held.Remove(node, out var pickup))
            return;
        // Taken by another hand, or let go of anywhere but into a graph: nothing lands.
        if (node.IsDestroyed || grab.IsGrabbed || node.Slot is not { } slot || slot.Parent is not { } graph
            || graph.GetComponent<ScriptGraphSpace>() == null)
            return;
        var records = new List<IUndoBatch>();
        try
        {
            if (!ReferenceEquals(pickup.Graph, graph))
                return;
            Settle(slot.LocalPosition, pickup.Position, (a, b) => (a - b).Length < Rounding, records);
            Settle(slot.LocalRotation, pickup.Rotation, (a, b) => MathF.Abs(floatQ.Dot(a, b)) > 1f - Rounding, records);
            Settle(slot.LocalScale, pickup.Scale, (a, b) => (a - b).Length < Rounding, records);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        if (records.Count == 0)
            return;
        carries.Landed.Add((node, graph, records));
        if (carries.Queued)
            return;
        // A hand lets go of everything it holds one after another; the step is made once they all have.
        carries.Queued = true;
        world.RunSynchronously(() => RecordLanding(world, carries));
    }

    // A landed field still where the pickup had it, give or take rounding, goes back to exactly that and is no
    // part of the step; one that moved is.
    private static void Settle<T>(Sync<T> field, T before, Func<T, T, bool> same, List<IUndoBatch> records)
    {
        var after = field.Value;
        if (!same(after, before))
        {
            records.Add(new FieldEditUndoBatch(field, before, after, MoveStep));
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(after, before))
            field.Value = before;
    }

    // A node deleted, or taken out of its graph, or in a graph put away since it landed, has nowhere in this step
    // to go back to: a held Destroy lets go of its nodes first, and its own step is the one that brings them back.
    private static void RecordLanding(World world, Carries carries)
    {
        carries.Queued = false;
        var records = new List<IUndoBatch>();
        foreach (var (node, graph, landed) in carries.Landed)
        {
            if (!node.IsDestroyed && ReferenceEquals(node.Slot?.Parent, graph) && graph is { IsDestroyed: false, IsActive: true })
                records.AddRange(landed);
        }
        carries.Landed.Clear();
        if (records.Count > 0)
            InspectorUndo.Record(world, CompositeUndoBatch.Combine(MoveStep, records));
    }
}
