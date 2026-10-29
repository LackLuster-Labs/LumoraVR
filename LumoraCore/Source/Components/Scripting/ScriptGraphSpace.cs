// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lumora.Core.Components.Assets;
using Lumora.Core.Math;
using Lumora.Core.Persistence;
using Lumora.Core.Scripting;
using Lumora.Script;
using Lumora.Shading;

namespace Lumora.Core.Components.Scripting;

// The root of a graph being edited: its node slots are its direct children. A script's Host is the
// ScriptHost the graph packs into; a shader graph's ShaderSource is what compiles it into a material's
// shader, and a loose group of shader nodes has neither yet.
//
// Every peer checks the graph for itself a moment after the last edit, with the same compiler the host
// runs, so every peer paints the same nodes red without a word on the wire. Nothing here writes to the
// datamodel while checking. The graph is read here and compiled on a worker through the compile cache,
// so the check that says a graph is clean has also compiled what the host then runs.
//
// A packed graph keeps its slot on and draws nothing: no cards, no wires, nothing a beam or a hand can
// find. A closed material graph is packed like that and still makes its material's shader, checked a
// little less often than one being built; a script graph is packed like that when it has errors, and is
// checked the same way so its errors are known. Switching a graph's slot off is what stops it: its script stops
// running it, and its material keeps the shader it last made. A graph parked for an undo step is switched
// off in the graveyard; a graph joined into another is parked like that, and whatever a hand held from it
// now belongs to the other. -xlinka
[ComponentCategory("Hidden")]
public sealed class ScriptGraphSpace : Component
{
    public const double CheckDelaySeconds = 0.25;

    // How often a packed material graph is looked at for edits.
    public const double PackedPollSeconds = 0.5;

    // The node list is made again when a child comes or goes, and anyway this often, for a node whose
    // component arrived after its slot did.
    private const double NodeSweepSeconds = 1.0;

    public readonly SyncRef<ScriptHost> Host = new();

    public readonly Sync<GraphKind> Kind = new();

    public readonly SyncRef<ShaderGraphSource> ShaderSource = new();

    public readonly Sync<bool> Packed = new();

    // A graph someone unpacked stands still in the world while it is open, so an object that moves or spins
    // does not carry its graph off. Where it stands is kept here in world space, and every machine holds the
    // slot there itself with local drives on its place under the object: nothing is sent while the object
    // moves. A zero turn holds nothing and the graph rides its object, as one a script opened does. -xlinka
    [HideInInspector]
    public readonly Sync<float3> HoldAt = new();
    [HideInInspector]
    public readonly Sync<floatQ> HoldTurn = new(default(floatQ));
    [HideInInspector]
    public readonly Sync<float> HoldSize = new(1f);

    private FieldDrive<float3>? _heldAt;
    private FieldDrive<floatQ>? _heldTurn;
    private FieldDrive<float3>? _heldSize;

    private static readonly IReadOnlyList<string> NoProblems = Array.Empty<string>();

    // Drawn and in reach: not packed away, and switched on.
    public bool IsShown => !Packed.Value && Slot is { IsDestroyed: false, IsActive: true };

    public bool IsHeld => HoldTurn.Value.LengthSquared > 0.5f;

    // Holds the graph where its slot stands now.
    public void HoldHere()
    {
        if (Slot is not { IsDestroyed: false } slot)
            return;
        HoldAt.Value = slot.GlobalPosition;
        HoldTurn.Value = slot.GlobalRotation.Normalized;
        HoldSize.Value = slot.GlobalScale.x;
    }

    // Every frame on every machine while it is held: the slot's place under its object, worked out from where
    // the graph stands in the world.
    private void KeepHeld()
    {
        var slot = Slot;
        if (!IsHeld || slot?.Parent is not { IsDestroyed: false } parent)
        {
            LetGo();
            return;
        }
        if (_heldAt == null)
        {
            _heldAt = FieldDrive<float3>.CreateLocal(this);
            _heldAt.LocalValueOnly = true;
            _heldAt.DriveTarget(slot.LocalPosition);
            _heldTurn = FieldDrive<floatQ>.CreateLocal(this);
            _heldTurn.LocalValueOnly = true;
            _heldTurn.DriveTarget(slot.LocalRotation);
            _heldSize = FieldDrive<float3>.CreateLocal(this);
            _heldSize.LocalValueOnly = true;
            _heldSize.DriveTarget(slot.LocalScale);
        }
        _heldAt.SetValue(parent.GlobalPointToLocal(HoldAt.Value));
        _heldTurn!.SetValue((parent.GlobalRotation.Inverse * HoldTurn.Value.Normalized).Normalized);
        var outer = parent.GlobalScale;
        float size = HoldSize.Value > 1e-4f ? HoldSize.Value : 1f;
        _heldSize!.SetValue(new float3(size / NonZero(outer.x), size / NonZero(outer.y), size / NonZero(outer.z)));
    }

    private static float NonZero(float value) => MathF.Abs(value) > 1e-6f ? value : 1e-6f;

    private void LetGo()
    {
        _heldAt?.Dispose();
        _heldTurn?.Dispose();
        _heldSize?.Dispose();
        _heldAt = null;
        _heldTurn = null;
        _heldSize = null;
    }

    public GraphLanguage Language => GraphLanguage.For(Kind.Value);

    // Every graph in a world, so a tool can find the one under a swipe or a port to snap to without
    // walking the slot tree.
    private static readonly ConditionalWeakTable<World, List<ScriptGraphSpace>> Open = new();

    // The frame some graph of the world last read itself out for a check, plus one so a fresh entry matches none.
    private static readonly ConditionalWeakTable<World, StrongBox<ulong>> CheckTurns = new();

    private readonly Dictionary<ScriptGraphNode, List<string>> _problems = new();
    private readonly Dictionary<ScriptGraphNode, List<string>> _notes = new();
    private readonly List<string> _graphProblems = new();
    private readonly List<ScriptGraphNode> _nodes = new();
    private readonly HashSet<ScriptGraphNode> _held = new();
    private int _revision = int.MinValue;
    private double _changedAt;
    private bool _dirty = true;
    private ScriptGraphView? _view;
    private Task<GraphCheck>? _checking;
    private ScriptGraphBuild? _checkingBuild;
    private int _checkingRevision;
    private Slot? _watched;
    private bool _activeChangeQueued;
    private double _polledAt = double.NaN;
    private bool _wasPacked;
    private bool _fromOlderSave;
    private bool? _bodiesPacked;
    private int _bodiesCount = -1;
    private bool _nodesStale = true;
    private double _nodesSweptAt = double.NegativeInfinity;
    private readonly List<ScriptGraphNode> _letGo = new();

    // This machine's cards and wires for the graph.
    public ScriptGraphView? View => _view;

    public event Action<ScriptGraphSpace>? Checked;

    // How many checks have run on this peer.
    public int CheckCount { get; private set; }

    public bool IsChecked => CheckCount > 0 && !_dirty;

    public bool HasProblems => _problems.Count > 0 || _graphProblems.Count > 0;

    public IReadOnlyList<string> GraphProblems => _graphProblems;

    // A script graph's last compile; null for a shader graph.
    public CompileResult? LastCompile { get; private set; }

    // A shader graph's last compile; null for a script.
    public ShaderProgram? LastShader { get; private set; }

    // The graph as it stood at the last check, with the objects its bindings stand for.
    public ScriptGraphBuild? LastBuild { get; private set; }

    // The nodes as of this frame's look, held ones included: what the view draws from. Made again only
    // when the graph's children change, so a frame of a big open graph walks no slots for it.
    public IReadOnlyList<ScriptGraphNode> NodeList => _nodes;

    // Moves whenever NodeList is made again.
    public int NodeListVersion { get; private set; }

    public IReadOnlyList<string> ProblemsOf(ScriptGraphNode node)
        => _problems.TryGetValue(node, out var list) ? list : NoProblems;

    // What the last check warned about on a node without it being wrong: a card shows these, not in red.
    public IReadOnlyList<string> NotesOf(ScriptGraphNode node)
        => _notes.TryGetValue(node, out var list) ? list : NoProblems;

    // The node slots under this graph, and any node a hand is holding that was picked up from here.
    public IEnumerable<ScriptGraphNode> Nodes
    {
        get
        {
            var slot = Slot;
            if (slot == null)
                yield break;
            foreach (var child in slot.Children)
            {
                if (child.IsDestroyed)
                    continue;
                var node = child.GetComponent<ScriptGraphNode>();
                if (node != null && !node.IsDestroyed)
                    yield return node;
            }
            foreach (var node in _held)
            {
                if (IsHeldFromHere(node))
                    yield return node;
            }
        }
    }

    private bool IsHeldFromHere(ScriptGraphNode node)
        => !node.IsDestroyed && node.Slot?.Parent != Slot && node.Slot?.GetComponent<Grabbable>() is { IsGrabbed: true } grab
           && ReferenceEquals(grab.LastParentRef.Target, Slot);

    // A node a hand holds that is let go of into this graph, which it now belongs to.
    internal void NoteHeld(ScriptGraphNode node)
    {
        if (node != null && IsHeldFromHere(node))
            _held.Add(node);
    }

    // Every graph this machine knows in a world, open or parked.
    public static void GetAll(World? world, List<ScriptGraphSpace> into)
    {
        into.Clear();
        if (world == null || !Open.TryGetValue(world, out var spaces))
            return;
        lock (spaces)
        {
            foreach (var space in spaces)
            {
                if (!space.IsDestroyed)
                    into.Add(space);
            }
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();
        if (World is { } world)
        {
            var spaces = Open.GetValue(world, static _ => new List<ScriptGraphSpace>());
            lock (spaces)
                spaces.Add(this);
        }
        _watched = Slot;
        if (_watched != null)
        {
            _watched.ActiveChanged += OnActiveChanged;
            _watched.OnChildAdded += OnChildrenChanged;
            _watched.OnChildRemoved += OnChildrenChanged;
        }
    }

    private void OnChildrenChanged(Slot parent, Slot child) => _nodesStale = true;

    // A graph saved before Packed existed was put away by switching its slot off. It loads put away the way
    // graphs are now, packed with its slot on, since an off slot now means a stopped graph.
    public override void Load(DataTreeNode node, LoadControl control)
    {
        base.Load(node, control);
        _fromOlderSave = !Packed.ValueCameFromData;
    }

    public override void OnStart()
    {
        base.OnStart();
        if (_fromOlderSave && World is { } world)
            world.RunSynchronously(TakeOlderSave);
    }

    private void TakeOlderSave()
    {
        _fromOlderSave = false;
        if (IsDestroyed || Slot is not { IsDestroyed: false } slot || slot.ActiveSelf.Value)
            return;
        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            Packed.Value = true;
            slot.ActiveSelf.Value = true;
        }
    }

    // Packing a script graph in place, or opening one, changes which graph its script runs.
    public override void OnChanges()
    {
        base.OnChanges();
        if (Packed.Value == _wasPacked)
            return;
        _wasPacked = Packed.Value;
        if (World is { } world && Host.Target is { IsDestroyed: false } host && (world.IsAuthority || host.Mode.Value == ScriptMode.Local))
            host.MarkChangeDirty();
    }

    // Put off until the change has landed whole: parking moves the slot and switches it off, and from
    // another machine those arrive one at a time.
    private void OnActiveChanged(Slot slot)
    {
        if (_activeChangeQueued || World == null)
            return;
        _activeChangeQueued = true;
        World.RunSynchronously(AfterActiveChanged);
    }

    private void AfterActiveChanged()
    {
        _activeChangeQueued = false;
        var world = World;
        if (IsDestroyed || world == null || Slot == null)
            return;
        if (!Slot.IsActive)
        {
            _view?.Dispose();
            _view = null;
            HandOverHeld();
            if (ShaderSource.Target is { IsDestroyed: false } source)
                source.CompileIfNone(this);
        }
        // Its script stops while the graph is switched off, so it decides again either way, in the change pass
        // like any change to the host. A live swap drops what the old runtime still had queued, and deciding
        // before the script phase could swap away a press made this frame.
        if (Host.Target is { IsDestroyed: false } host && (world.IsAuthority || host.Mode.Value == ScriptMode.Local))
            host.MarkChangeDirty();
    }

    // What a hand held from here while this graph was joined into another now belongs to that one.
    private void HandOverHeld()
    {
        foreach (var node in _held)
        {
            if (!node.IsDestroyed && !IsHeldFromHere(node) && node.Space is { } now && !ReferenceEquals(now, this))
                now.NoteHeld(node);
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Switched off, it is stopped, and nobody is looking at it.
        if (Slot?.IsActive != true)
        {
            LetGo();
            return;
        }

        bool packed = Packed.Value;
        KeepHeld();
        if (World?.IsAuthority == true)
            KeepBodies(packed);
        if (packed && _view != null)
        {
            _view.Dispose();
            _view = null;
            HandOverHeld();
        }
        // A graph packed as it is, with its errors, is still looked at now and then, so whoever asks can tell.
        double now = World?.Time.TotalTime ?? 0.0;
        // Closed graphs that came up together would all poll in the same frame, every time: each takes its own
        // place in the half second instead.
        if (double.IsNaN(_polledAt))
            _polledAt = now - PackedPollSeconds * (1.0 - PollPhase());
        if (!packed || now - _polledAt >= PackedPollSeconds)
        {
            _polledAt = now;
            RefreshNodes(now);
            int revision = Revision();
            if (revision != _revision)
            {
                _revision = revision;
                _changedAt = now;
                _dirty = true;
            }
        }
        if (_checking is { IsCompleted: true })
            FinishCheck();
        if (_dirty && _checking == null && now - _changedAt >= CheckDelaySeconds && TakeCheckTurn())
            BeginCheck();

        if (packed)
            return;
        _view ??= new ScriptGraphView(this);
        _view.Update();
    }

    // On the host: a packed graph's nodes have their grab boxes and grabs switched off, so nothing of a graph
    // nobody can see is in anyone's way, and a shown graph's are switched back on. Packing or opening a graph
    // only changes Packed, and this follows it, through undo and redo as well. Walked again only when that
    // or the number of nodes changes. -xlinka
    private void KeepBodies(bool packed)
    {
        int count = Slot.ChildCount;
        if (_bodiesPacked == packed && _bodiesCount == count)
            return;
        _bodiesPacked = packed;
        _bodiesCount = count;
        bool shown = !packed;
        using (World?.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var node in Nodes)
            {
                if (node.Slot is not { IsDestroyed: false } at)
                    continue;
                if (at.GetComponent<BoxCollider>() is { } box && box.Enabled.Value != shown)
                    box.Enabled.Value = shown;
                if (at.GetComponent<Grabbable>() is { } grab)
                {
                    if (grab.Enabled.Value != shown)
                        grab.Enabled.Value = shown;
                    if (grab.Scalable.Value)
                        grab.Scalable.Value = false;
                }
            }
        }
    }

    public override void OnDestroy()
    {
        if (_watched != null)
        {
            _watched.ActiveChanged -= OnActiveChanged;
            _watched.OnChildAdded -= OnChildrenChanged;
            _watched.OnChildRemoved -= OnChildrenChanged;
        }
        _watched = null;
        if (World is { } world && Open.TryGetValue(world, out var spaces))
        {
            lock (spaces)
                spaces.Remove(this);
        }
        _view?.Dispose();
        _view = null;
        LetGo();
        if (World != null && Host.Target is { IsDestroyed: false } host && (World.IsAuthority || host.Mode.Value == ScriptMode.Local))
            World.RunSynchronously(host.Reconcile);
        base.OnDestroy();
    }

    // Where in the poll period this graph sits: its id spread evenly over [0, 1) by a golden-ratio hash, so
    // graphs made one after another land far apart.
    private double PollPhase() => ((ReferenceID.RawValue * 0x9E3779B97F4A7C15UL) >> 11) / (double)(1UL << 53);

    // Reading a graph out for a check is main-thread work as big as the graph, so one graph of a world does it
    // a frame. A world that opens with many waiting spreads them over that many frames.
    private bool TakeCheckTurn()
    {
        if (World is not { } world)
            return true;
        var turn = CheckTurns.GetOrCreateValue(world);
        ulong frame = world.Time.UpdateIndex + 1;
        if (turn.Value == frame)
            return false;
        turn.Value = frame;
        return true;
    }

    // Reads the graph as it stands and hands it to a worker to check with its kind's compiler.
    private void BeginCheck()
    {
        var build = ScriptGraphPacking.Build(this, withPositions: false);
        _checkingBuild = build;
        _checkingRevision = _revision;
        _checking = Language.CheckAsync(build.Graph);
    }

    // What the worker found, unless the graph has been edited since it was read; the next check is then
    // already on its way.
    private void FinishCheck()
    {
        var result = _checking!.Result;
        var build = _checkingBuild!;
        _checking = null;
        _checkingBuild = null;
        if (_checkingRevision != _revision)
            return;

        _problems.Clear();
        _notes.Clear();
        _graphProblems.Clear();
        foreach (var (node, message) in build.Problems)
            Note(_problems, node, message);
        // A shader graph's warnings go on its cards as notes; a script's cards show its errors as they always have.
        foreach (var diagnostic in result.Diagnostics)
        {
            bool error = diagnostic.Severity == DiagnosticSeverity.Error;
            if (!error && Kind.Value != GraphKind.Shader)
                continue;
            var into = error ? _problems : _notes;
            if (diagnostic.NodeId != null && build.Nodes.TryGetValue(diagnostic.NodeId, out var node))
                Note(into, node, diagnostic.Message);
            else if (error)
                Note(into, null, diagnostic.Message);
        }

        LastCompile = result.Script;
        LastShader = result.Shader;
        LastBuild = build;
        _dirty = false;
        CheckCount++;
        Checked?.Invoke(this);

        // The authority runs an open graph as it stands, and so does every machine for a Local script, so
        // the host hears about every check. A shader graph's material follows it on every machine.
        if (World != null && Host.Target is { IsDestroyed: false } host && (World.IsAuthority || host.Mode.Value == ScriptMode.Local))
            host.Reconcile();
        if (ShaderSource.Target is { IsDestroyed: false } source && result.Shader != null)
            source.TakeCheck(this, result.Shader);
    }

    private void Note(Dictionary<ScriptGraphNode, List<string>> into, ScriptGraphNode? node, string message)
    {
        if (node == null)
        {
            if (!_graphProblems.Contains(message))
                _graphProblems.Add(message);
            return;
        }
        if (!into.TryGetValue(node, out var list))
            into[node] = list = new List<string>();
        if (!list.Contains(message))
            list.Add(message);
    }

    // Every node's content folded into one number, for whatever compiles a graph nobody has open.
    public int ContentRevision()
    {
        var hash = new HashCode();
        int count = 0;
        foreach (var node in Nodes)
        {
            hash.Add(node.ReferenceID.RawValue);
            hash.Add(node.ContentHash());
            count++;
        }
        hash.Add(count);
        return hash.ToHashCode();
    }

    // A node a hand took from here leaves the children, and one let go of comes back, so the list is made
    // again on those; while a hand holds one it is looked at every frame.
    private void RefreshNodes(double now)
    {
        if (!_nodesStale && _held.Count == 0 && now - _nodesSweptAt < NodeSweepSeconds)
            return;
        foreach (var node in _nodes)
        {
            if (IsHeldFromHere(node))
                _held.Add(node);
        }
        if (_held.Count > 0)
        {
            HandOverHeld();
            _letGo.Clear();
            foreach (var node in _held)
            {
                if (!IsHeldFromHere(node))
                    _letGo.Add(node);
            }
            foreach (var node in _letGo)
                _held.Remove(node);
        }
        _nodes.Clear();
        foreach (var node in Nodes)
            _nodes.Add(node);
        _nodesStale = false;
        _nodesSweptAt = now;
        NodeListVersion++;
    }

    // Cheap enough to take every frame for a graph of a thousand nodes: a node's content hash is only
    // walked again after an edit to it.
    private int Revision()
    {
        var hash = new HashCode();
        hash.Add(_nodes.Count);
        foreach (var node in _nodes)
        {
            hash.Add(node.ReferenceID.RawValue);
            hash.Add(node.ContentHash());
        }
        return hash.ToHashCode();
    }
}
