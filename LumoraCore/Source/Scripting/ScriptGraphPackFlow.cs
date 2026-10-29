// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Scripting;
using Lumora.Core.Localization;
using Lumora.Script;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Scripting;

// UNPACKING AND PACKING. Each one undo step for whoever does it, however many graphs it takes.
//
// Unpack writes a script's document out as node slots under its object, standing where ScriptGraphPlacement
// says: back where it was left when that is still in front of the opener, else in front of them, and held
// there in the world while its object moves (see ScriptGraphSpace). A graph
// whose cards would land on top of each other is laid out for reading first. A slot's many graphs open
// together, the ones with nowhere to go back to side by side as one wall in front of the opener.
//
// Pack reads the graph out on the world's thread, compiles and stores it on workers, then lands back on the
// world's thread: the document on the script, the pose the graph stood at, and the node slots parked for
// undo, all one step. A graph has one pack on its way at a time and asking again joins it. An edit made while
// it was on its way is never lost: landing reads the graph out again, and a graph that changed goes round
// once more. A node this machine's hand holds goes back into its graph first; a node in someone else's hand
// stops that graph's pack. Graphs packed together land together, and the ones that cannot pack stay open
// with the first reason. A graph with errors still packs: as it is, hidden with its nodes kept, its script
// keeping the last good graph it ran, stored as its document; unpacking it shows the errors again. The script
// never stops: it swaps from the open graph to the packed one with its stored values, and its object and
// slot stay switched on. -xlinka
public static partial class ScriptGraphEditing
{
    // How many times a pack reads a graph that keeps being edited before it gives up.
    private const int PackRounds = 3;

    private static readonly ConditionalWeakTable<ScriptGraphSpace, Task<string?>> Packing = new();

    public static bool IsPacking(ScriptGraphSpace? space)
        => space != null && Packing.TryGetValue(space, out var running) && !running.IsCompleted;

    // The open space if there is one, else the host's graph unpacked. Null, with the reason, when the
    // graph has not loaded on this machine yet.
    public static ScriptGraphSpace? Unpack(ScriptHost host, out string? error)
    {
        error = null;
        if (host == null || host.IsDestroyed || host.World == null)
        {
            error = "there is no script here";
            return null;
        }
        if (FindSpace(host) is { } open)
        {
            Reveal(open, UnpackStep);
            return open;
        }
        if (Document(host, out error) is not { } graph)
            return null;

        var world = host.World;
        var space = ScriptGraphPacking.Unpack(host, graph, world.LocalUser?.Root);
        InspectorUndo.Record(world, SlotExistenceUndoBatch.Created(world, new[] { space.Slot }, UnpackStep));
        return space;
    }

    // The graph a host runs, or an empty one for a host with none. Null while it is on its way.
    private static ScriptGraph? Document(ScriptHost host, out string? error)
    {
        error = null;
        if (host.Graph.Target == null)
            return new ScriptGraph();
        // Mid-switch, the old graph would still read as loaded, and editing it would lose the new one.
        if (host.Graph.Target is IUrlAssetProvider { IsLoadPending: true })
        {
            error = "its graph is still loading here";
            return null;
        }
        if (host.Graph.IsAssetAvailable && host.Graph.Asset?.Graph is { } loaded)
            return loaded;
        error = (host.Graph.Target as IUrlAssetProvider)?.LoadFailure ?? host.Graph.Asset?.LoadError ?? "its graph has not loaded here yet";
        return null;
    }

    // A graph put away is shown again, and switched on: opening a graph is for working on it. One put away
    // somewhere out of the opener's reach comes in front of them.
    internal static void Reveal(ScriptGraphSpace space, LocaleText step)
    {
        var records = new List<IUndoBatch>();
        var slot = space.Slot;
        if ((space.Packed.Value || !slot.ActiveSelf.Value) && space.World?.LocalUser?.Root is { } opener)
        {
            var block = Block(space.Nodes);
            if (!ScriptGraphPlacement.InReach(opener, slot, block))
            {
                var position = slot.LocalPosition.Value;
                var rotation = slot.LocalRotation.Value;
                var scale = slot.LocalScale.Value;
                var holdAt = space.HoldAt.Value;
                var holdTurn = space.HoldTurn.Value;
                var holdSize = space.HoldSize.Value;
                ScriptGraphPlacement.InFrontOf(opener, block).Apply(slot);
                space.HoldHere();
                NoteMove(records, slot.LocalPosition, position, step);
                NoteMove(records, slot.LocalRotation, rotation, step);
                NoteMove(records, slot.LocalScale, scale, step);
                NoteMove(records, space.HoldAt, holdAt, step);
                NoteMove(records, space.HoldTurn, holdTurn, step);
                NoteMove(records, space.HoldSize, holdSize, step);
            }
        }
        if (space.Packed.Value)
        {
            space.Packed.Value = false;
            records.Add(new FieldEditUndoBatch(space.Packed, true, false, step));
        }
        if (!slot.ActiveSelf.Value)
        {
            slot.ActiveSelf.Value = true;
            records.Add(new FieldEditUndoBatch(slot.ActiveSelf, false, true, step));
        }
        if (records.Count > 0 && space.World is { } world)
            Record(world, step, records);
    }

    private static void NoteMove<T>(List<IUndoBatch> records, Sync<T> field, T before, LocaleText step)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, before))
            records.Add(new FieldEditUndoBatch(field, before, field.Value, step));
    }

    // The graphs under a slot, what a slot card held in a hand works on: every script anywhere under it,
    // packed or open, and the material graphs on orbs under it, closed or open. Open means not packed, on
    // or switched off: either way it is there to pack.
    public readonly record struct SlotGraphs(List<ScriptHost> Packed, List<ScriptGraphSpace> Open,
        List<ShaderGraphSource> Parked, List<ShaderGraphSource> Shown)
    {
        public bool AnyPacked => Packed.Count + Parked.Count > 0;

        public bool AnyOpen => Open.Count + Shown.Count > 0;

        public bool Any => AnyPacked || AnyOpen;
    }

    public static SlotGraphs GraphsUnder(Slot root)
    {
        var graphs = new SlotGraphs(new List<ScriptHost>(), new List<ScriptGraphSpace>(), new List<ShaderGraphSource>(),
            new List<ShaderGraphSource>());
        if (root == null || root.IsDestroyed)
            return graphs;
        foreach (var host in root.GetComponentsInChildren<ScriptHost>())
        {
            if (host.IsDestroyed)
                continue;
            if (FindSpace(host) is { Packed.Value: false } space)
                graphs.Open.Add(space);
            else
                graphs.Packed.Add(host);
        }
        foreach (var source in root.GetComponentsInChildren<ShaderGraphSource>())
        {
            if (source.IsDestroyed || source.Graph.Target is not { IsDestroyed: false } graph)
                continue;
            (graph.Packed.Value ? graphs.Parked : graphs.Shown).Add(source);
        }
        return graphs;
    }

    // Every packed script under a slot unpacked, as one undo step, and the material graphs under it opened
    // over their orbs. What opened, and the first refusal.
    public static List<ScriptGraphSpace> UnpackAll(Slot root, out string? error)
    {
        error = null;
        var opened = new List<ScriptGraphSpace>();
        var world = root?.World;
        if (root == null || root.IsDestroyed || world == null)
        {
            error = "that object is gone";
            return opened;
        }
        var graphs = GraphsUnder(root);
        var undo = FindUndo(world);
        var wall = new List<ScriptGraphSpace>();
        using (undo != null ? undo.BeginBatch(UnpackStep) : default)
        {
            foreach (var host in graphs.Packed)
            {
                try
                {
                    bool made = FindSpace(host) == null;
                    if (Unpack(host, out var refused) is { } space)
                    {
                        opened.Add(space);
                        if (made && !ScriptGraphPlacement.IsBack(space, host))
                            wall.Add(space);
                    }
                    else
                    {
                        error ??= refused;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    error ??= $"you may not unpack the script on {host.Slot.Name.Value}";
                }
            }
            if (wall.Count > 1 && world.LocalUser?.Root is { } opener)
                StandSideBySide(opener, wall);
            foreach (var source in graphs.Parked)
            {
                if (ShaderGraphMaterials.SetOpen(source, true) && source.Graph.Target is { } space)
                    opened.Add(space);
                else
                    error ??= $"you may not open the graph of {source.Slot.Name.Value}";
            }
        }
        return opened;
    }

    private const float WallWidth = 12f;
    private const float WallMaxWidth = 48f;
    private const float WallHeight = 6f;
    private const float WallGap = 0.3f;

    // The scripts of one object sit within a hand of each other, so thirty graphs opened one by one would
    // stand inside each other. Opened together they go up as one wall in front of the opener: rows a gap
    // apart, tallest at the bottom, each row centred. The rows start WallWidth wide and widen until the wall
    // is no taller than WallHeight, so the top rows stay within reach. -xlinka
    private static void StandSideBySide(UserRoot opener, List<ScriptGraphSpace> spaces)
    {
        var boxes = new List<(ScriptGraphSpace Space, float Left, float Right, float Top, float Bottom)>();
        foreach (var space in spaces)
        {
            var block = Block(space.Nodes);
            boxes.Add((space, block.Left, block.Right, block.Top, block.Bottom));
        }
        boxes.Sort((a, b) => (b.Top - b.Bottom).CompareTo(a.Top - a.Bottom));
        float limit = WallWidth;
        foreach (var box in boxes)
            limit = MathF.Max(limit, box.Right - box.Left);

        var rows = new List<(float Width, float Height, int First, int Count)>();
        float tall;
        while (true)
        {
            rows.Clear();
            for (int i = 0; i < boxes.Count; i++)
            {
                float width = boxes[i].Right - boxes[i].Left;
                float height = boxes[i].Top - boxes[i].Bottom;
                if (rows.Count == 0 || rows[^1].Width + WallGap + width > limit)
                {
                    rows.Add((width, height, i, 1));
                    continue;
                }
                var row = rows[^1];
                rows[^1] = (row.Width + WallGap + width, MathF.Max(row.Height, height), row.First, row.Count + 1);
            }
            tall = -WallGap;
            float wide = 0f;
            foreach (var row in rows)
            {
                tall += row.Height + WallGap;
                wide = MathF.Max(wide, row.Width);
            }
            if (tall <= WallHeight || limit >= WallMaxWidth)
            {
                limit = wide;
                break;
            }
            limit += 2f;
        }

        var frame = ScriptGraphPlacement.InFrontOf(opener, limit, tall);
        float y = -tall * 0.5f;
        foreach (var (rowWidth, rowHeight, first, count) in rows)
        {
            float x = -rowWidth * 0.5f;
            for (int i = first; i < first + count; i++)
            {
                var (space, left, right, _, bottom) = boxes[i];
                var local = new Math.float3(x - left, y - bottom, 0f) * frame.Scale;
                new ScriptGraphPlacement.Frame(frame.Position + frame.Rotation * local, frame.Rotation, frame.Scale).Apply(space.Slot);
                space.HoldHere();
                x += right - left + WallGap;
            }
            y += rowHeight + WallGap;
        }
    }

    // Every open script graph under a slot packed back, and the material graphs under it closed onto their
    // orbs, all as one undo step. The first refusal, or null.
    public static Task<string?> PackAllAsync(Slot root)
    {
        if (root == null || root.IsDestroyed || root.World is not { } world)
            return Task.FromResult<string?>("that object is gone");
        var graphs = GraphsUnder(root);
        string? closing = null;
        void Close()
        {
            foreach (var source in graphs.Shown)
            {
                if (!ShaderGraphMaterials.SetOpen(source, false))
                    closing ??= $"you may not close the graph of {source.Slot.Name.Value}";
            }
        }
        if (graphs.Open.Count == 0)
        {
            var undo = FindUndo(world);
            using (undo != null ? undo.BeginBatch(PackStep) : default)
                Close();
            return Task.FromResult(closing);
        }
        var pack = PackTogether(graphs.Open, world, Close);
        return pack.ContinueWith(done => done.Result ?? closing, TaskScheduler.Default);
    }

    // Nodes standing on their own packed into an object picked by its card: the object's script takes them
    // in, a new script when it has none and its graph unpacked first when it has one, and is packed. They
    // go beside what the script already holds. Taking them in is one undo step and the pack another.
    public static Task<string?> PackIntoAsync(ScriptGraphSpace group, Slot target)
    {
        var world = group?.World;
        if (group == null || group.IsDestroyed || world == null)
            return Task.FromResult<string?>("there is no graph to pack");
        if (group.Kind.Value == GraphKind.Shader)
            return Task.FromResult<string?>("a shader graph stays with its material; there is nothing to pack");
        if (!IsImplicit(group))
            return Task.FromResult<string?>("that graph is a script of its own already; only nodes standing on their own pack into an object");
        if (target == null || target.IsDestroyed || target.IsRootSlot)
            return Task.FromResult<string?>("pick an object to pack into");
        if (group.Slot?.Parent is { } loose && (ReferenceEquals(target, loose) || target.IsDescendantOf(loose)))
            return Task.FromResult<string?>("those nodes cannot pack into themselves");

        ScriptGraphSpace? into;
        var undo = FindUndo(world);
        using (undo != null ? undo.BeginBatch(JoinStep) : default)
        {
            into = EditScriptOn(target, out var error);
            if (into == null)
                return Task.FromResult<string?>(error ?? "that object's script could not be opened");
            var already = new HashSet<ScriptGraphNode>(into.Nodes);
            var merge = ScriptMergeUndo.Take(into, group, out error);
            if (merge == null)
                return Task.FromResult<string?>(error);
            InspectorUndo.Record(world, merge);
            Beside(into, already);
        }
        return PackAsync(into);
    }

    // The nodes that just came into a graph as a block, to the right of the ones it had, tops level; in a
    // graph that had none, centred on its origin. Undoing the join puts them back where they were.
    private static void Beside(ScriptGraphSpace space, HashSet<ScriptGraphNode> already)
    {
        var came = new List<ScriptGraphNode>();
        foreach (var node in space.Nodes)
        {
            if (!already.Contains(node) && node.Slot is { IsDestroyed: false })
                came.Add(node);
        }
        if (came.Count == 0)
            return;
        var (left, right, top, _) = Extent(came);
        Math.float3 move;
        if (already.Count > 0)
        {
            var had = Extent(already);
            move = new Math.float3(had.Right + 0.1f - left, had.Top - top, 0f);
        }
        else
        {
            move = new Math.float3(-(left + right) * 0.5f, -top, 0f);
        }
        foreach (var node in came)
        {
            try
            {
                node.Slot.LocalPosition.Value += move;
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // The cards' block in their graph's own metres; left past right when there are none.
    private static (float Left, float Right, float Top, float Bottom) Extent(IEnumerable<ScriptGraphNode> nodes)
    {
        float left = float.MaxValue, right = float.MinValue, top = float.MinValue, bottom = float.MaxValue;
        foreach (var node in nodes)
        {
            if (node.Slot is not { IsDestroyed: false } slot)
                continue;
            var at = slot.LocalPosition.Value;
            var layout = ScriptCardLayout.For(node, 0);
            left = MathF.Min(left, at.x);
            right = MathF.Max(right, at.x + layout.Width * ScriptEditorStyle.CardScale);
            top = MathF.Max(top, at.y);
            bottom = MathF.Min(bottom, at.y - layout.Height * ScriptEditorStyle.CardScale);
        }
        return (left, right, top, bottom);
    }

    private static ShaderGraphBuilder.Bounds Block(IEnumerable<ScriptGraphNode> nodes)
    {
        var (left, right, top, bottom) = Extent(nodes);
        return left <= right ? new ShaderGraphBuilder.Bounds(left, right, top, bottom) : new ShaderGraphBuilder.Bounds(0f, 0.3f, 0f, -0.2f);
    }

    public static Task<string?> PackAsync(ScriptGraphSpace space)
    {
        var world = space?.World;
        if (space == null || world == null || space.IsDestroyed)
            return Task.FromResult<string?>("there is no graph to pack");
        return PackTogether(new[] { space }, world, null);
    }

    // Graphs packed as one undo step. A shader graph is passed over: it closes, it never packs.
    public static Task<string?> PackTogetherAsync(IEnumerable<ScriptGraphSpace> spaces)
    {
        var list = new List<ScriptGraphSpace>();
        foreach (var space in spaces)
        {
            if (space is { IsDestroyed: false } && !list.Contains(space))
                list.Add(space);
        }
        if (list.Count == 0 || list[0].World is not { } world)
            return Task.FromResult<string?>("there is no graph to pack");
        return PackTogether(list, world, null);
    }

    // Starts on the world's thread, which is where reading a graph out has to happen. A graph already on its
    // way is joined rather than packed twice. Whatever alsoInStep does lands in the same undo step.
    private static Task<string?> PackTogether(IReadOnlyList<ScriptGraphSpace> spaces, World world, Action? alsoInStep)
    {
        var joined = new List<Task<string?>>();
        var fresh = new List<ScriptGraphSpace>();
        foreach (var space in spaces)
        {
            if (Packing.TryGetValue(space, out var running) && !running.IsCompleted)
                joined.Add(running);
            else
                fresh.Add(space);
        }
        if (fresh.Count == 0 && alsoInStep == null)
            return FirstRefusal(joined);
        var mine = RunPack(fresh, world, alsoInStep);
        foreach (var space in fresh)
            Packing.AddOrUpdate(space, mine);
        if (joined.Count == 0)
            return mine;
        joined.Add(mine);
        return FirstRefusal(joined);
    }

    private static async Task<string?> FirstRefusal(List<Task<string?>> packs)
    {
        string? first = null;
        foreach (var refused in await Task.WhenAll(packs).ConfigureAwait(false))
            first ??= refused;
        return first;
    }

    private sealed class PackJob
    {
        public required ScriptGraphSpace Space;
        public required ScriptGraphBuild Build;
        public required string Read;
        public Uri? Url;
        public string? Refusal;
        // Why the graph packs as it is, hidden with its nodes, and the node it is about.
        public string? Errors;
        public string? ErrorNode;
        // The last good graph its script runs, when that is not the one it has stored, and its bindings.
        public ScriptGraph? Keep;
        public IWorldElement?[]? KeepBindings;
    }

    private sealed class PackTally
    {
        public int Packed;
        public int Refused;
        public string? First;
        public string? Note;

        public void Refuse(string why)
        {
            Refused++;
            First ??= why;
        }

        public string? Answer() => First == null ? Note
            : Packed == 0 && Refused == 1 ? First
            : $"{First} ({Refused} of {Packed + Refused} graphs left open)";
    }

    private static async Task<string?> RunPack(List<ScriptGraphSpace> spaces, World world, Action? alsoInStep)
    {
        var tally = new PackTally();
        var jobs = new List<PackJob>();
        foreach (var space in spaces)
        {
            if (ReadOut(space, tally) is { } job)
                jobs.Add(job);
        }
        for (int round = 1; jobs.Count > 0 || alsoInStep != null; round++)
        {
            await Task.WhenAll(jobs.Select(Prepare)).ConfigureAwait(false);
            var current = jobs;
            var step = alsoInStep;
            bool more = round < PackRounds;
            jobs = await OnWorldThread(world, () =>
            {
                try
                {
                    return Land(world, current, more, tally, step);
                }
                catch (Exception ex)
                {
                    LumoraLogger.Warn($"Loom: pack failed: {ex}");
                    tally.Refuse("packing failed: " + ex.Message);
                    return new List<PackJob>();
                }
            }).ConfigureAwait(false);
            alsoInStep = null;
        }
        return tally.Answer();
    }

    // On the world's thread: the graph read out as it stands, or why it cannot pack.
    private static PackJob? ReadOut(ScriptGraphSpace space, PackTally tally)
    {
        if (space.IsDestroyed || space.World == null)
        {
            tally.Refuse("the graph is gone");
            return null;
        }
        if (space.Kind.Value == GraphKind.Shader)
        {
            tally.Refuse("a shader graph stays with its material; there is nothing to pack");
            return null;
        }
        if (space.Host.Target is not { IsDestroyed: false } host)
        {
            tally.Refuse("the graph has no script to pack into");
            return null;
        }
        if (LetGoOfNodes(space) is { } held)
        {
            tally.Refuse(held);
            return null;
        }
        RenameClashingIds(space);
        var build = ScriptGraphPacking.Build(space);
        var job = new PackJob { Space = space, Build = build, Read = build.Graph.ToJson() };
        if (build.Problems.Count > 0)
            job.Errors = Describe(build.Problems[0].Node, build.Problems[0].Message);
        KeepLastGood(job, host);
        return job;
    }

    // What a graph with errors packs as: the graph its script runs now, kept as its document when it is not the
    // one stored already. Read up front, on the world's thread, since the errors may only show in the compile.
    private static void KeepLastGood(PackJob job, ScriptHost host)
    {
        if (host.Running is not { } running)
            return;
        var stored = host.Graph.IsAssetAvailable ? host.Graph.Asset?.Graph : null;
        if (stored != null && (ReferenceEquals(stored, running.Graph) || Logic(stored) == Logic(running.Graph)))
            return;
        job.Keep = running.Graph;
        job.KeepBindings = new IWorldElement?[running.Bindings.Count];
        for (int i = 0; i < job.KeepBindings.Length; i++)
            job.KeepBindings[i] = ShaderPropertyFields.Unwrap(running.Bindings[i]) as IWorldElement;
    }

    // What a graph does, without where its cards stand.
    private static string Logic(ScriptGraph graph)
    {
        var copy = ScriptGraph.Parse(graph.ToUtf8Json());
        foreach (var node in copy.Nodes)
        {
            node.Position = Math.float3.Zero;
            node.Rotation = Math.floatQ.Identity;
        }
        return copy.ComputeHash();
    }

    // A node in this machine's hand goes back into its graph first, so nothing of the graph is left in a
    // hand when its slots go. One in someone else's hand stops the pack.
    private static string? LetGoOfNodes(ScriptGraphSpace space)
    {
        var local = space.World?.LocalUser;
        foreach (var node in space.Nodes.ToList())
        {
            if (node.Slot?.GetComponent<Grabbable>() is not { IsGrabbed: true } grab)
                continue;
            if (grab.Grabber is not { } hand || !ReferenceEquals(hand.OwningUser, local))
                return $"{node.Title} is in someone else's hand";
            hand.Release(grab);
        }
        return null;
    }

    // On a worker: the compile and the store. A graph that does not compile is packed as it is, and what is
    // stored then is the last good graph its script runs, if it is not stored already.
    private static async Task Prepare(PackJob job)
    {
        try
        {
            if (job.Errors == null)
            {
                var compiled = await ScriptEnvironment.CompileAsync(job.Build.Graph).ConfigureAwait(false);
                if (!compiled.Success)
                {
                    var diagnostic = compiled.Errors.FirstOrDefault();
                    job.Errors = diagnostic?.Message ?? "it does not compile";
                    job.ErrorNode = diagnostic?.NodeId;
                }
            }
            var keep = job.Errors == null ? job.Build.Graph : job.Keep;
            if (keep == null)
                return;
            job.Url = await ScriptGraphAsset.StoreAsync(keep).ConfigureAwait(false);
            if (job.Url == null)
                job.Refusal = "there is no local asset store to keep the graph in";
        }
        catch (Exception ex)
        {
            job.Refusal = "the graph could not be stored: " + ex.Message;
        }
    }

    // On the world's thread: every graph that still reads out as it was packed lands, as one undo step. A graph
    // edited meanwhile is read again for another round, while rounds are left.
    private static List<PackJob> Land(World world, List<PackJob> jobs, bool more, PackTally tally, Action? alsoInStep)
    {
        var again = new List<PackJob>();
        var ready = new List<PackJob>();
        foreach (var job in jobs)
        {
            if (job.Refusal != null)
            {
                tally.Refuse(job.Refusal);
                continue;
            }
            if (job.Space.IsDestroyed || job.Space.Host.Target is not { IsDestroyed: false } host)
            {
                tally.Refuse("the graph or its script went away while packing");
                continue;
            }
            if (LetGoOfNodes(job.Space) is { } held)
            {
                tally.Refuse(held);
                continue;
            }
            var now = ScriptGraphPacking.Build(job.Space);
            string read = now.Graph.ToJson();
            if (read == job.Read)
            {
                ready.Add(job);
                continue;
            }
            if (!more)
            {
                tally.Refuse($"{Name(job.Space)} kept changing while it packed; pack it again");
                continue;
            }
            var next = new PackJob { Space = job.Space, Build = now, Read = read };
            if (now.Problems.Count > 0)
                next.Errors = Describe(now.Problems[0].Node, now.Problems[0].Message);
            KeepLastGood(next, host);
            again.Add(next);
        }
        if (ready.Count == 0 && alsoInStep == null)
            return again;

        var undo = FindUndo(world);
        using (undo != null ? undo.BeginBatch(PackStep) : default)
        {
            foreach (var job in ready)
            {
                try
                {
                    if (Apply(job) is { } refused)
                    {
                        tally.Refuse(refused);
                        continue;
                    }
                    tally.Packed++;
                    if (job.Errors != null)
                        tally.Note ??= $"{Name(job.Space)} has errors, so it was packed as it is and its script keeps its last good graph: {Explain(job)}";
                }
                catch (UnauthorizedAccessException)
                {
                    tally.Refuse($"you may not pack {Name(job.Space)}");
                }
            }
            alsoInStep?.Invoke();
        }
        return again;
    }

    // A compile error names its node by id; the card's title reads better.
    private static string Explain(PackJob job)
    {
        if (job.ErrorNode == null)
            return job.Errors!;
        job.Build.Nodes.TryGetValue(job.ErrorNode, out var node);
        return Describe(node, job.Errors!);
    }

    private static string Name(ScriptGraphSpace space)
        => space.Host.Target?.Slot?.Name.Value is { Length: > 0 } name ? name : "the graph";

    // The document onto the script, the pose the graph stood at, and the node slots parked; or, for a graph with
    // errors, the last good graph onto the script when it was not stored already and the nodes kept, hidden.
    private static string? Apply(PackJob job)
    {
        var space = job.Space;
        var world = space.World;
        var host = space.Host.Target;
        if (world == null || space.IsDestroyed || host == null || host.IsDestroyed)
            return "the graph or its script went away while packing";

        var undo = FindUndo(world);
        using var scope = undo != null ? undo.BeginBatch(PackStep) : default;

        // Asked before the graph is stored on the host, after which it is a group no longer.
        bool group = IsImplicit(space);
        bool asItIs = job.Errors != null;
        var packer = world.LocalUser;
        var target = host;
        if (job.Url is { } url)
        {
            byte owner = host.ReferenceID.GetUserByte();
            bool worldContent = owner == RefIDConstants.AUTHORITY_BYTE;
            if (!worldContent && (packer == null || !packer.HoldsByte(owner)) && host.Slot.MayRemoveComponent(host))
                target = Remint(host);

            var provider = target.Graph.Target as ScriptGraphProvider;
            if (provider == null || provider.IsDestroyed || !ReferenceEquals(provider.Slot, target.Slot)
                || provider.ReferenceID.GetUserByte() != target.ReferenceID.GetUserByte())
            {
                provider = target.Slot.AttachComponent<ScriptGraphProvider>();
                ComponentUndo.RecordAttach(target, provider);
            }

            var beforeUrl = provider.URL.Value;
            provider.URL.Value = url;
            InspectorUndo.Record(world, new FieldEditUndoBatch(provider.URL, beforeUrl, url, PackStep));

            var beforeGraph = target.Graph.Value;
            target.Graph.Target = provider;
            if (!beforeGraph.Equals(target.Graph.Value))
                InspectorUndo.Record(world, new FieldEditUndoBatch(target.Graph, beforeGraph, target.Graph.Value, PackStep));

            var beforeBindings = ScriptBindingsUndo.Snapshot(target.Bindings);
            IWorldElement?[] afterBindings;
            if (asItIs)
            {
                afterBindings = job.KeepBindings ?? Array.Empty<IWorldElement?>();
            }
            else
            {
                afterBindings = new IWorldElement?[job.Build.Bindings.Count];
                for (int i = 0; i < afterBindings.Length; i++)
                    afterBindings[i] = job.Build.Bindings[i];
            }
            bool rebuild = world.IsAuthority || packer?.HoldsByte(target.ReferenceID.GetUserByte()) == true;
            ScriptBindingsUndo.Assign(target.Bindings, afterBindings, rebuild);
            InspectorUndo.Record(world, new ScriptBindingsUndo(target.Bindings, beforeBindings, afterBindings, PackStep, rebuild));
        }

        // Where it stood, for the next unpack.
        var placed = new List<IUndoBatch>();
        ScriptGraphPlacement.Remember(space, target, placed, PackStep);
        foreach (var record in placed)
            InspectorUndo.Record(world, record);

        // A packed group is a Script from now on.
        var named = target.Slot.Name;
        if (group && named.Value != ScriptSlotName)
        {
            var beforeName = named.Value;
            named.Value = ScriptSlotName;
            if (named.Value == ScriptSlotName)
                InspectorUndo.Record(world, new FieldEditUndoBatch(named, beforeName, ScriptSlotName, PackStep));
        }

        if (asItIs)
            PackInPlace(world, space, target);
        else
            RemoveSpace(world, undo, space, target);
        return null;
    }

    // Parked for the undo step, or destroyed. Someone who may not delete another's node slots packs them in
    // place instead.
    private static void RemoveSpace(World world, UndoManager? undo, ScriptGraphSpace space, ScriptHost host)
    {
        try
        {
            if (undo != null && SlotExistenceUndoBatch.Destroy(world, new[] { space.Slot }) is { } removed)
                undo.Record(removed);
            else
                space.Slot.Destroy();
        }
        catch (UnauthorizedAccessException)
        {
            if (space.Slot.IsDestroyed)
                return;
            try
            {
                PackInPlace(world, space, host);
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // The nodes kept and hidden, pointed at the script they now pack into, so the next unpack finds and shows
    // them.
    private static void PackInPlace(World world, ScriptGraphSpace space, ScriptHost host)
    {
        var beforeHost = space.Host.Value;
        if (!ReferenceEquals(space.Host.Target, host))
        {
            space.Host.Target = host;
            InspectorUndo.Record(world, new FieldEditUndoBatch(space.Host, beforeHost, space.Host.Value, PackStep));
        }
        if (!space.Packed.Value)
        {
            space.Packed.Value = true;
            InspectorUndo.Record(world, new FieldEditUndoBatch(space.Packed, false, true, PackStep));
        }
    }

    // The packer's own host in place of someone else's: same slot, same settings, new author.
    private static ScriptHost Remint(ScriptHost old)
    {
        var fresh = old.Slot.AttachComponent<ScriptHost>();
        fresh.Mode.Value = old.Mode.Value;
        fresh.Enabled.Value = old.Enabled.Value;
        fresh.GraphAt.Value = old.GraphAt.Value;
        fresh.GraphTurn.Value = old.GraphTurn.Value;
        ComponentUndo.RecordAttach(fresh, fresh);
        ComponentUndo.RecordDestroy(old, old);
        return fresh;
    }

    // Only on the packer's machine, right before it packs: every other peer only reads the ids.
    private static void RenameClashingIds(ScriptGraphSpace space)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in space.Nodes)
        {
            string id = node.NodeId.Value ?? string.Empty;
            if (GraphNode.IsValidId(id) && taken.Add(id))
                continue;
            string fresh = node.FreshId();
            for (int i = 2; !taken.Add(fresh); i++)
                fresh = node.FreshId() + "_" + i;
            node.NodeId.Value = fresh;
        }
    }

    private const string LocalOnly = "a Local script changes only what it made on this machine";

    // A script's Unpack Graph: the open graph when there is one, else the host's graph written out under it,
    // made the way the script makes any slot. Refused, with the reason, while the graph has not loaded on the
    // host. No undo step: a script that wants one records the space itself.
    internal static ScriptGraphSpace? UnpackFor(ScriptContext ctx, ScriptHost? host)
    {
        if (ctx.OffHost)
            throw new UnauthorizedAccessException(LocalOnly);
        if (!ScriptReads.Reached(ctx, host, "script"))
            return null;
        if (FindSpace(host) is { } open)
        {
            if (open.Packed.Value)
                ScriptWrites.Write(ctx, open.Packed, false);
            if (!open.Slot.ActiveSelf.Value)
                ScriptWrites.Write(ctx, open.Slot.ActiveSelf, true);
            return open;
        }

        var graph = Document(host, out var error) ?? throw new InvalidOperationException(error);
        var made = ScriptWrites.MakeUnder(ctx, host.Slot, 1 + graph.Nodes.Count, () => ScriptGraphPacking.Unpack(host, graph).Slot);
        return made.GetComponent<ScriptGraphSpace>();
    }

    // A script's Pack Graph: the graph in space packed in place, onto the script it is open on, the way a person's
    // pack does it, as the packing script's author. Only a graph on one of that author's own scripts packs: a
    // world script's on the world's, a guest's on their own, or someone else's script would run what another
    // wrote. Its compile runs on a worker and the document is stored as the editor stores it; once both land
    // the script takes it and the nodes go. A graph with errors packs as it is, hidden with its nodes, its
    // script keeping its last good graph. Gives back the script's object.
    internal static Slot? PackFor(ScriptContext ctx, ScriptGraphSpace? space)
    {
        if (ctx.OffHost)
            throw new UnauthorizedAccessException(LocalOnly);
        var world = ctx.World;
        if (!ScriptReads.Reached(ctx, space, "graph"))
            return null;
        if (!world.IsAuthority)
        {
            ctx.Fail("graphs are packed on the host");
            return null;
        }
        if (space.Kind.Value == GraphKind.Shader)
            throw new InvalidOperationException("a shader graph stays with its material; there is nothing to pack");
        if (space.Host.Target is not { IsDestroyed: false } host)
            throw new InvalidOperationException("the graph has no script to pack into");
        if (NotPackersOwn(ctx.Principal, space) is { } refused)
            throw new UnauthorizedAccessException(refused);
        if (!AuthorsScript(ctx.Principal, host))
            throw new UnauthorizedAccessException("a script packs a graph only onto its own author's scripts");

        var job = new PackJob { Space = space, Build = ScriptGraphPacking.Build(space), Read = string.Empty };
        if (job.Build.Problems.Count > 0)
            job.Errors = Describe(job.Build.Problems[0].Node, job.Build.Problems[0].Message);
        else if (space.IsChecked && space.HasProblems)
            job.Errors = FirstProblem(space);
        KeepLastGood(job, host);
        world.Scripts.Packed(ctx);
        var principal = ctx.Principal;
        world.Scripts.AwaitPack(Prepare(job), () => LandInPlace(world, principal, job));
        return host.Slot;
    }

    private static bool AuthorsScript(ScriptPrincipal principal, ScriptHost host)
    {
        byte owner = host.ReferenceID.GetUserByte();
        return principal.IsWorldContent ? owner == RefIDConstants.AUTHORITY_BYTE : owner == principal.AllocationByte;
    }

    // Why a script may not pack a graph as its own, or null when it may. A world script packs a graph open on
    // one of the world's own scripts, where every edit already runs as the world. A guest's script packs only
    // its author's own work and the world's, and nothing anyone else wrote, or someone else's graph would run
    // with its author's rights.
    internal static string? NotPackersOwn(ScriptPrincipal principal, ScriptGraphSpace space)
    {
        if (principal.IsWorldContent)
        {
            return space.Host.Target is { IsDestroyed: false } host && host.ReferenceID.GetUserByte() == RefIDConstants.AUTHORITY_BYTE
                && ReferenceEquals(FindSpace(host), space)
                ? null
                : "a world script packs only a graph open on one of the world's own scripts";
        }
        byte author = principal.AllocationByte;
        var user = principal.Author;
        foreach (var node in space.Nodes)
        {
            if (!HeldBy(node, author) || WrittenByOther(node.NodeType, user) || WrittenByOther(node.Signature, user)
                || WrittenByOther(node.Version, user) || WrittenByOther(node.NodeId, user))
                return OnlyOwnWork;
            foreach (var value in node.Values)
            {
                if (!HeldBy(value, author) || WrittenByOther(value.Port, user) || WrittenByOther(value.Json, user)
                    || WrittenByOther(value.Reference, user))
                    return OnlyOwnWork;
            }
            foreach (var wire in node.Inputs)
            {
                if (!HeldBy(wire, author) || WrittenByOther(wire.Port, user) || WrittenByOther(wire.Node, user) || WrittenByOther(wire.NodePort, user))
                    return OnlyOwnWork;
            }
            foreach (var wire in node.Exits)
            {
                if (!HeldBy(wire, author) || WrittenByOther(wire.Port, user) || WrittenByOther(wire.Node, user) || WrittenByOther(wire.NodePort, user))
                    return OnlyOwnWork;
            }
        }
        return null;
    }

    private const string OnlyOwnWork = "a guest's script packs only its author's own work and the world's";

    private static bool HeldBy(IWorldElement element, byte author)
    {
        byte owner = element.ReferenceID.GetUserByte();
        return owner == author || owner == RefIDConstants.AUTHORITY_BYTE;
    }

    private static bool WrittenByOther(Networking.Sync.ConflictingSyncElement member, User? author)
        => member.LastModifyingUser is { } writer && !ReferenceEquals(writer, author);

    private static string FirstProblem(ScriptGraphSpace space)
    {
        if (space.GraphProblems.Count > 0)
            return space.GraphProblems[0];
        foreach (var node in space.Nodes)
        {
            var problems = space.ProblemsOf(node);
            if (problems.Count > 0)
                return Describe(node, problems[0]);
        }
        return "it does not compile";
    }

    // Once the compile and the store have landed, on the host, as the packing script's author and in their
    // range: the document onto the script, where the graph stood, and the nodes gone; or for a graph with errors,
    // the last good graph onto the script when it was not stored already and the nodes kept, hidden. No undo
    // step: a script that wants one records it itself.
    private static void LandInPlace(World world, ScriptPrincipal principal, PackJob job)
    {
        var space = job.Space;
        if (world.IsDestroyed || space.IsDestroyed || space.Host.Target is not { IsDestroyed: false } host
            || !principal.IsStillAuthorIn(world))
            return;
        if (job.Refusal != null)
        {
            LumoraLogger.Warn($"Loom [{principal}]: a graph was not packed: {job.Refusal}");
            return;
        }
        using (world.DataModelPermissions?.EnterActor(principal))
        {
            using (ScriptWrites.RangeOf(world, principal))
            {
                if (job.Url is { } url)
                {
                    var provider = host.Graph.Target as ScriptGraphProvider;
                    if (provider == null || provider.IsDestroyed || !ReferenceEquals(provider.Slot, host.Slot)
                        || provider.ReferenceID.GetUserByte() != host.ReferenceID.GetUserByte())
                        provider = host.Slot.AttachComponent<ScriptGraphProvider>();
                    provider.URL.Value = url;
                    host.Graph.Target = provider;
                    IWorldElement?[] bindings;
                    if (job.Errors != null)
                    {
                        bindings = job.KeepBindings ?? Array.Empty<IWorldElement?>();
                    }
                    else
                    {
                        bindings = new IWorldElement?[job.Build.Bindings.Count];
                        for (int i = 0; i < bindings.Length; i++)
                            bindings[i] = job.Build.Bindings[i];
                    }
                    ScriptBindingsUndo.Assign(host.Bindings, bindings, rebuild: true);
                    if (!principal.IsWorldContent)
                    {
                        var decides = new List<IField> { host.Graph, provider.URL };
                        for (int i = 0; i < host.Bindings.Count; i++)
                            decides.Add(host.Bindings.GetElement(i));
                        world.Scripts.RememberSetUp(host, decides);
                    }
                }
                ScriptGraphPlacement.Remember(space, host, new List<IUndoBatch>(), PackStep);
                if (job.Errors != null)
                {
                    space.Packed.Value = true;
                    return;
                }
                try
                {
                    space.Slot.Destroy();
                }
                catch (UnauthorizedAccessException)
                {
                    space.Packed.Value = true;
                }
            }
        }
    }

    private static Task<T> OnWorldThread<T>(World world, Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.RunSynchronously(() => done.SetResult(action()));
        return done.Task;
    }
}
