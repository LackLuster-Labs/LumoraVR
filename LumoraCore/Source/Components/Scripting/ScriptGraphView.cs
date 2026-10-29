// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Scripting;

namespace Lumora.Core.Components.Scripting;

// What a graph looks like on this machine: a card per node and one mesh for every wire, all on local
// slots, kept up with the shared node data a frame at a time. A card is rebuilt only when its node, that
// node's problems or its fault mark change; the wires are rebuilt when any end of any wire moves, or a cut
// marks or lets go of one.
//
// A frame of an open graph asks each node one cheap question, whether it was edited, moved or held, and
// does real work only for what did. Laying every wire out again each frame and hashing every node twice
// was most of what thirty converted graphs open at once cost, at a standstill. -xlinka
//
// Flow shows as glowing segments running along the wires it takes, from the running script's counts of how often it
// left each node by each exit: what the host publishes for a Shared script, this machine's own runtime for
// a Local one. A shader graph's cards show instead what each node makes, in a preview compiled here after
// every check.
//
// Cards and previews are made inside a few milliseconds a frame that every open graph in the world shares,
// so a big graph fills in over a second instead of freezing one frame for it. A card whose node changed goes
// first, so an edit shows at once; the rest come nearest the viewer first.
//
// A script's wires answer a node tool's beam along the curves they are drawn on, and the wire a beam is on is
// drawn thicker and brighter, with a bead where a press puts a relay. -xlinka
public sealed class ScriptGraphView
{
    public const double SignalSeconds = 0.65;

    // How far from a port a wire is still the card's to answer, in metres: the dot and the reach around it.
    public const float WireEndClear = 0.02f;
    private const float HoverWidth = 1.9f;
    private const float HoverLift = 0.45f;
    private const double SignalGap = 0.09;
    private const int MaxBurst = 3;
    private const int MaxSignals = 256;
    public const double BuildBudgetMs = 6.0;
    // While cards are still coming, the wire mesh is remade every few frames rather than with every card.
    private const int FillingWireEvery = 8;
    // The wires are laid out again this often anyway, for an end moved by something this view does not see.
    private const double WireSweepSeconds = 1.5;
    // A view the shared allowance has passed over this many frames makes one card anyway.
    private const ulong StarveFrames = 8;
    // A wire fired this many times running, each within StreamGap of the last, is streaming; it stops once
    // it has not fired for StreamHold.
    private const int StreamRun = 4;
    private const double StreamGap = 0.2;
    private const double StreamHold = 0.4;
    // How fast the streaming glow runs down a wire, in metres a second.
    private const float StreamSpeed = 1.1f;

    private static readonly long BudgetTicks = (long)(BuildBudgetMs * Stopwatch.Frequency / 1000.0);
    private static readonly ConditionalWeakTable<World, FrameBudget> Budgets = new();

    private sealed class FrameBudget
    {
        public ulong Frame = ulong.MaxValue;
        public long Spent;

        public void Spend(long since) => Spent += Stopwatch.GetTimestamp() - since;
    }

    private readonly ScriptGraphSpace _space;
    private readonly List<(ScriptGraphNode Node, int Stamp, string? Fault, bool Rebuild)> _due = new();
    private readonly HashSet<ScriptGraphNode> _stalePreviews = new();
    private readonly List<ScriptGraphNode> _byDistance = new();
    private int _waitingCards;
    private readonly Dictionary<ScriptGraphNode, ScriptNodeCard> _cards = new();
    private readonly Dictionary<ScriptGraphNode, ScriptNodePreview> _previews = new();
    private readonly Dictionary<ScriptGraphNode, string> _documentIds = new();
    private int _previewCheck = -1;
    private Lumora.Core.Components.Assets.ShaderGraphSource? _previewSource;
    private readonly List<ScriptGraphNode> _nodes = new();
    private readonly List<ScriptGraphNode> _gone = new();
    private readonly List<ScriptWireCurve> _curves = new();
    private readonly List<ScriptWireKey> _keys = new();
    private readonly Dictionary<ScriptWireKey, int> _flowWires = new();
    private readonly HashSet<ScriptWireKey> _cutMarks = new();
    private readonly Dictionary<ScriptWireKey, int> _seenCounts = new();
    private readonly List<ScriptWireKey> _goneTraceKeys = new();
    private readonly List<(ScriptWireKey Key, double Start)> _signals = new();
    private readonly List<ScriptWireCurve> _streaks = new();
    private readonly Dictionary<ScriptGraphNode, (string Text, Dictionary<string, int> Counts)> _published = new();
    private readonly Dictionary<(string Node, string Exit), int> _localTraces = new();
    private object? _localTracesFor;
    private Slot? _wireSlot;
    private ScriptWireMesh? _wireMesh;
    private Slot? _signalSlot;
    private ScriptWireMesh? _signalMesh;
    private int _wireStamp;
    private bool _hasSampledTraces;
    private float3 _wireMin;
    private float3 _wireMax;
    private readonly Dictionary<int, ScriptWireHit> _hovers = new();
    private readonly List<ScriptWireCurve> _hoverCurves = new();
    private readonly List<(float3 Center, float Radius, color Tint)> _hoverBeads = new();
    private Slot? _hoverSlot;
    private ScriptWireMesh? _hoverMesh;
    private Slot? _markSlot;
    private ScriptPulseMesh? _mark;
    private int _hoverStamp;
    private readonly HashSet<ScriptGraphNode> _nodeSet = new();
    private readonly Dictionary<ScriptWireKey, double> _lastSignal = new();
    private int _nodesVersion = -1;
    private int _shapeStamp;
    private bool _wiresDirty = true;
    private double _wiresAt = double.NegativeInfinity;
    private ulong _builtFrame;
    private readonly Dictionary<ScriptWireKey, (double Last, int Run)> _fireRuns = new();
    private readonly HashSet<ScriptWireKey> _streams = new();
    private readonly List<ScriptWireKey> _streamsGone = new();
    private readonly List<ScriptWireCurve> _streamCurves = new();
    private bool _streamsDirty;
    private Slot? _streamSlot;
    private ScriptWireMesh? _streamMesh;
    private FresnelMaterial? _streamMaterial;
    private readonly List<ScriptCardSlab> _slabs = new();
    private Slot? _backingSlot;
    private ScriptCardBackings? _backings;
    private int _slabStamp;

    public ScriptGraphView(ScriptGraphSpace space)
    {
        _space = space;
        _builtFrame = space.World?.Time.UpdateIndex ?? 0UL;
    }

    public int CardCount => _cards.Count;

    public int WireCount => _wireMesh?.WireCount ?? 0;

    // Execution pulses on their way along wires right now.
    public int SignalCount => _signals.Count;

    public ScriptNodeCard? CardOf(ScriptGraphNode node) => _cards.TryGetValue(node, out var card) ? card : null;

    // A shader node's preview on this machine, once its graph has been checked.
    public ScriptNodePreview? PreviewOf(ScriptGraphNode node) => _previews.TryGetValue(node, out var preview) ? preview : null;

    public int PreviewCount => _previews.Count;

    // Every card made and every preview compiled for the graph as it stands.
    public bool Settled => _waitingCards == 0 && _stalePreviews.Count == 0;

    public void Update()
    {
        var world = _space.World;
        double now = world?.Time.TotalTime ?? 0.0;
        var budget = BudgetOf(world);
        bool listChanged = _nodesVersion != _space.NodeListVersion;
        if (listChanged)
        {
            _nodesVersion = _space.NodeListVersion;
            _nodes.Clear();
            _nodeSet.Clear();
            foreach (var node in _space.NodeList)
            {
                _nodes.Add(node);
                _nodeSet.Add(node);
            }
            _wiresDirty = true;
        }

        _due.Clear();
        var shape = new HashCode();
        foreach (var node in _nodes)
        {
            if (node.IsDestroyed)
                continue;
            string? fault = FaultOf(node, now);
            int stamp = Stamp(node, fault);
            bool hasCard = _cards.TryGetValue(node, out var card);
            AddShape(ref shape, node, card);
            if (hasCard)
            {
                if (card!.Stamp == stamp || card.IsEditing)
                {
                    card.Refresh();
                    continue;
                }
                _due.Add((node, stamp, fault, true));
            }
            else
            {
                _due.Add((node, stamp, fault, false));
            }
        }
        if (BuildDue(budget))
            _wiresDirty = true;
        int shapeStamp = shape.ToHashCode();
        if (shapeStamp != _shapeStamp)
        {
            _shapeStamp = shapeStamp;
            _wiresDirty = true;
        }

        if (listChanged || _cards.Count > _nodes.Count)
        {
            _gone.Clear();
            foreach (var node in _cards.Keys)
            {
                if (node.IsDestroyed || !_nodeSet.Contains(node))
                    _gone.Add(node);
            }
            foreach (var node in _gone)
            {
                _cards[node].Destroy();
                _cards.Remove(node);
                _published.Remove(node);
                _stalePreviews.Remove(node);
                if (_previews.Remove(node, out var preview))
                    preview.Destroy();
            }
            if (_gone.Count > 0)
                _wiresDirty = true;
        }

        if (now - _wiresAt >= WireSweepSeconds)
            _wiresDirty = true;
        if (_wiresDirty && (_waitingCards == 0 || _wireMesh == null || (world?.Time.UpdateIndex ?? 0UL) % FillingWireEvery == 0))
        {
            _wiresDirty = false;
            _wiresAt = now;
            UpdateWires();
            UpdateBackings();
        }
        UpdateHover();
        UpdateSignals(now);
        UpdatePreviews(budget);
        UpdateSelection();
    }

    // The frame's shared allowance, started afresh on the world's first look at it each frame.
    private static FrameBudget BudgetOf(World? world)
    {
        if (world == null)
            return new FrameBudget();
        var budget = Budgets.GetOrCreateValue(world);
        ulong frame = world.Time.UpdateIndex;
        if (budget.Frame != frame)
        {
            budget.Frame = frame;
            budget.Spent = 0;
        }
        return budget;
    }

    // What moves a wire's ends: the node's edits, where it sits, the card it wears and whether that card's
    // drawer is open. A node a hand holds is somewhere else every frame, so where it is in the world counts.
    private void AddShape(ref HashCode shape, ScriptGraphNode node, ScriptNodeCard? card)
    {
        var slot = node.Slot;
        shape.Add(node.EditVersion);
        if (ReferenceEquals(slot.Parent, _space.Slot))
        {
            shape.Add(slot.LocalPosition.Value);
            shape.Add(slot.LocalRotation.Value);
            shape.Add(slot.LocalScale.Value);
        }
        else
        {
            shape.Add(slot.GlobalPosition);
            shape.Add(slot.GlobalRotation);
        }
        shape.Add(card != null ? RuntimeHelpers.GetHashCode(card) : 0);
        shape.Add(card?.Layout.OptionsOpen == true);
    }

    // Changed cards first, then the missing ones nearest the viewer, until the frame's allowance is spent. A
    // changed card is made past the allowance, so an edit shows at once, and so is one card of a view the
    // allowance has passed over for a few frames, so a graph never stalls behind another one's. It was one
    // card a frame past the allowance for every view, and thirty graphs opened together spent five times the
    // allowance every frame until they were all built. A rebuilt card keeps its old self up until its new
    // one replaces it. Whether any card was made. -xlinka
    private bool BuildDue(FrameBudget budget)
    {
        _waitingCards = _due.Count;
        if (_due.Count == 0)
            return false;
        ulong frame = _space.World?.Time.UpdateIndex ?? 0UL;
        bool starved = frame - _builtFrame >= StarveFrames;
        if (budget.Spent >= BudgetTicks && !starved && !_due.Exists(due => due.Rebuild))
            return false;
        var viewer = Viewer();
        _due.Sort((a, b) => a.Rebuild != b.Rebuild ? (a.Rebuild ? -1 : 1)
            : float3.DistanceSquared(a.Node.Slot.GlobalPosition, viewer).CompareTo(float3.DistanceSquared(b.Node.Slot.GlobalPosition, viewer)));
        bool first = true;
        bool made = false;
        foreach (var (node, stamp, fault, rebuild) in _due)
        {
            if (budget.Spent >= BudgetTicks && !(first && (rebuild || starved)))
                break;
            first = false;
            made = true;
            _builtFrame = frame;
            long since = Stopwatch.GetTimestamp();
            _cards.TryGetValue(node, out var old);
            old?.Destroy();
            _cards[node] = ScriptNodeCard.Build(node, _space.ProblemsOf(node), stamp, fault, old?.Layout.OptionsOpen == true,
                NotesOf(node));
            _waitingCards--;
            budget.Spend(since);
        }
        return made;
    }

    // Where this machine looks from, for building what is nearest first; the graph's own place without a user.
    private float3 Viewer()
    {
        var root = _space.World?.LocalUser?.Root;
        return root != null ? root.HeadPosition : _space.Slot.GlobalPosition;
    }

    // A selected node's card wears the selection tint, and a hold about to select or deselect some ramps theirs:
    // one lookup a card each frame, and a write only when one changes.
    private void UpdateSelection()
    {
        var selection = ScriptNodeSelection.Of(_space.World);
        foreach (var (node, card) in _cards)
        {
            float amount = selection?.Highlight(node) ?? 0f;
            if (card.Highlight != amount)
                card.SetHighlight(amount);
        }
    }

    // Each shader node's output on its own, compiled here from what the graph's last check read, again after
    // every check; a card rebuilt in between keeps its preview. The compiles share the frame's allowance with
    // the cards, nearest first, and a preview whose program came out the same is left as it is. Nothing for a
    // script.
    private void UpdatePreviews(FrameBudget budget)
    {
        if (_space.Kind.Value != GraphKind.Shader)
            return;
        var build = _space.LastBuild;
        if (build != null && _space.CheckCount != _previewCheck)
        {
            _documentIds.Clear();
            foreach (var (id, node) in build.Nodes)
                _documentIds[node] = id;
            _previewCheck = _space.CheckCount;
            foreach (var node in _previews.Keys)
                _stalePreviews.Add(node);
        }
        // A loose graph given its material from its output's well: its previews take the material's textures.
        var current = _space.ShaderSource.Target is { IsDestroyed: false } live ? live : null;
        if (!ReferenceEquals(current, _previewSource))
        {
            _previewSource = current;
            foreach (var node in _previews.Keys)
                _stalePreviews.Add(node);
        }
        foreach (var node in _nodes)
        {
            if (!_cards.TryGetValue(node, out var card) || !card.Layout.Preview)
            {
                _stalePreviews.Remove(node);
                if (_previews.Remove(node, out var stale))
                    stale.Destroy();
                continue;
            }
            if (!_previews.TryGetValue(node, out var preview))
            {
                preview = ScriptNodePreview.Create(node);
                _previews[node] = preview;
                _stalePreviews.Add(node);
            }
            preview.Place(card.Layout);
        }
        if (build == null || _stalePreviews.Count == 0)
            return;

        var textures = _space.ShaderSource.Target is { IsDestroyed: false } source ? source : null;
        var viewer = Viewer();
        _byDistance.Clear();
        _byDistance.AddRange(_stalePreviews);
        _byDistance.Sort((a, b) => float3.DistanceSquared(a.Slot.GlobalPosition, viewer).CompareTo(float3.DistanceSquared(b.Slot.GlobalPosition, viewer)));
        bool first = true;
        foreach (var node in _byDistance)
        {
            if (!first && budget.Spent >= BudgetTicks)
                break;
            first = false;
            long since = Stopwatch.GetTimestamp();
            _stalePreviews.Remove(node);
            if (_documentIds.TryGetValue(node, out var id) && _previews.TryGetValue(node, out var preview))
                preview.Show(Lumora.Shading.ShaderGraphCompiler.CompilePreview(build.Graph, GraphLanguage.Shader.Registry, id), textures);
            budget.Spend(since);
        }
    }

    public void Dispose()
    {
        foreach (var card in _cards.Values)
            card.Destroy();
        _cards.Clear();
        foreach (var preview in _previews.Values)
            preview.Destroy();
        _previews.Clear();
        foreach (var slot in new[] { _wireSlot, _signalSlot, _hoverSlot, _markSlot, _streamSlot, _backingSlot })
        {
            if (slot != null && !slot.IsDestroyed)
                slot.Destroy();
        }
        _wireSlot = null;
        _wireMesh = null;
        _signalSlot = null;
        _signalMesh = null;
        _hoverSlot = null;
        _hoverMesh = null;
        _markSlot = null;
        _mark = null;
        _streamSlot = null;
        _streamMesh = null;
        _streamMaterial = null;
        _backingSlot = null;
        _backings = null;
        _streams.Clear();
        _hovers.Clear();
    }

    // Where a port of a node meets its wires, in world space.
    public bool TryPortPoint(ScriptGraphNode node, ScriptCardHandleKind kind, string port, out float3 point)
    {
        point = default;
        if (!_cards.TryGetValue(node, out var card) || !card.Layout.TryAnchor(kind, port, out var anchor))
            return false;
        point = node.Slot.LocalPointToGlobal(ScriptEditorStyle.CanvasToLocal(anchor));
        return true;
    }

    // The wires a cut would take, drawn red until it lets go. Null or empty lets go of them all.
    public void SetCutMarks(IReadOnlyCollection<ScriptWireKey>? keys)
    {
        if (_cutMarks.Count > 0 || keys?.Count > 0)
            _wiresDirty = true;
        _cutMarks.Clear();
        if (keys == null)
            return;
        foreach (var key in keys)
            _cutMarks.Add(key);
    }

    // Every wire the straight line from a to b crosses, both ends in the graph's own space, judged flat on
    // the graph's plane against the curve the wire is drawn along.
    public int WiresCrossing(float3 a, float3 b, List<ScriptWireKey> into)
    {
        into.Clear();
        for (int i = 0; i < _curves.Count; i++)
        {
            var curve = _curves[i];
            var previous = ScriptWireMesh.PointOn(curve, 0f);
            for (int step = 1; step < ScriptWireMesh.Rings; step++)
            {
                var next = ScriptWireMesh.PointOn(curve, step / (float)(ScriptWireMesh.Rings - 1));
                if (Crosses(a, b, previous, next))
                {
                    into.Add(_keys[i]);
                    break;
                }
                previous = next;
            }
        }
        return into.Count;
    }

    // The wire a ray passes within reach of, reach in metres: the one it passes nearest, never within WireEndClear
    // of a port or over a card, which are the cards' to answer. Tested against the curve each wire is drawn along,
    // behind a box around its control points, so a ray nowhere near a wire costs a box test.
    public bool TryHitWire(float3 rayOrigin, float3 rayDirection, float maxDistance, float reach, out ScriptWireHit hit)
    {
        hit = default;
        var slot = _space.Slot;
        if (_curves.Count == 0 || slot == null || slot.IsDestroyed)
            return false;
        var origin = slot.GlobalPointToLocal(rayOrigin);
        // A metre of the ray in the graph's space, so how far along it a point is reads straight in metres.
        var step = slot.GlobalPointToLocal(rayOrigin + rayDirection) - origin;
        if (step.LengthSquared < 1e-12f)
            return false;
        float near = slot.GlobalScaleToLocal(reach);
        float clear = slot.GlobalScaleToLocal(WireEndClear);
        var pad = new float3(near, near, near);
        if (!RayBox(origin, step, _wireMin - pad, _wireMax + pad, maxDistance))
            return false;

        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < _curves.Count; i++)
        {
            var curve = _curves[i];
            ScriptWireMesh.ControlPoints(curve, out var p0, out var p1, out var p2, out var p3);
            if (!RayBox(origin, step, Min(Min(p0, p1), Min(p2, p3)) - pad, Max(Max(p0, p1), Max(p2, p3)) + pad, maxDistance))
                continue;
            float gap = float.MaxValue, along = 0f, distance = 0f;
            var point = float3.Zero;
            var previous = ScriptWireMesh.PointOn(curve, 0f);
            for (int sample = 1; sample < ScriptWireMesh.Rings; sample++)
            {
                var next = ScriptWireMesh.PointOn(curve, sample / (float)(ScriptWireMesh.Rings - 1));
                Nearest(origin, step, previous, next, out float s, out float u, out float apart);
                var on = previous + (next - previous) * u;
                if (apart <= near && apart < gap && s <= maxDistance && (on - p0).Length >= clear && (on - p3).Length >= clear)
                {
                    gap = apart;
                    distance = s;
                    point = on;
                    along = (sample - 1 + u) / (ScriptWireMesh.Rings - 1);
                }
                previous = next;
            }
            if (gap >= best || OverCard(point))
                continue;
            best = gap;
            found = true;
            hit = new ScriptWireHit(_keys[i], along, point, distance, rayOrigin + rayDirection * distance);
        }
        return found;
    }

    // The wire each pointer is on, or null to let go of it.
    public void SetWireHover(int pointer, ScriptWireHit? hit)
    {
        if (hit is { } on)
            _hovers[pointer] = on;
        else
            _hovers.Remove(pointer);
    }

    // Which wire a pointer is on, as this machine draws it.
    public ScriptWireKey? HoveredWire(int pointer) => _hovers.TryGetValue(pointer, out var on) ? on.Key : null;

    // Where a ray, one step a metre, comes nearest a segment: metres along the ray, how far along the segment,
    // and how far apart the two are there.
    private static void Nearest(in float3 origin, in float3 step, in float3 a, in float3 b, out float s, out float u, out float apart)
    {
        var e = b - a;
        var w = origin - a;
        float aa = float3.Dot(step, step);
        float ab = float3.Dot(step, e);
        float bb = float3.Dot(e, e);
        float d = float3.Dot(step, w);
        float f = float3.Dot(e, w);
        float denominator = aa * bb - ab * ab;
        u = denominator > 1e-12f ? System.Math.Clamp((aa * f - ab * d) / denominator, 0f, 1f) : 0f;
        s = MathF.Max(0f, (ab * u - d) / aa);
        u = bb > 1e-12f ? System.Math.Clamp((f + ab * s) / bb, 0f, 1f) : 0f;
        apart = (w + step * s - e * u).Length;
    }

    // Whether a point in the graph's space is on a card or one of its port dots.
    private bool OverCard(in float3 point)
    {
        float margin = ScriptEditorStyle.HandleSize * ScriptEditorStyle.CardScale;
        foreach (var (node, card) in _cards)
        {
            if (node.IsDestroyed || !ReferenceEquals(node.Slot?.Parent, _space.Slot))
                continue;
            var at = node.Slot.LocalPosition.Value;
            float width = card.Layout.Width * ScriptEditorStyle.CardScale;
            float height = card.Layout.Height * ScriptEditorStyle.CardScale;
            if (point.x >= at.x - margin && point.x <= at.x + width + margin && point.y <= at.y + margin && point.y >= at.y - height - margin)
                return true;
        }
        return false;
    }

    private static bool RayBox(in float3 origin, in float3 step, in float3 min, in float3 max, float maxDistance)
    {
        float near = 0f;
        float far = maxDistance;
        return Slab(origin.x, step.x, min.x, max.x, ref near, ref far) && Slab(origin.y, step.y, min.y, max.y, ref near, ref far)
            && Slab(origin.z, step.z, min.z, max.z, ref near, ref far);
    }

    private static bool Slab(float origin, float step, float min, float max, ref float near, ref float far)
    {
        if (MathF.Abs(step) < 1e-9f)
            return origin >= min && origin <= max;
        float inverse = 1f / step;
        float enter = (min - origin) * inverse;
        float exit = (max - origin) * inverse;
        if (enter > exit)
            (enter, exit) = (exit, enter);
        near = MathF.Max(near, enter);
        far = MathF.Min(far, exit);
        return near <= far;
    }

    private static float3 Min(in float3 a, in float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static float3 Max(in float3 a, in float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));

    private static bool Crosses(in float3 a, in float3 b, in float3 c, in float3 d)
    {
        float d1 = Side(c, d, a);
        float d2 = Side(c, d, b);
        float d3 = Side(a, b, c);
        float d4 = Side(a, b, d);
        return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
    }

    private static float Side(in float3 from, in float3 to, in float3 point)
        => (to.x - from.x) * (point.y - from.y) - (to.y - from.y) * (point.x - from.x);

    private int Stamp(ScriptGraphNode node, string? fault)
    {
        var hash = new HashCode();
        hash.Add(node.ContentHash());
        foreach (var problem in _space.ProblemsOf(node))
            hash.Add(problem);
        foreach (var note in _space.NotesOf(node))
            hash.Add(note);
        hash.Add(node.SignatureRefusal);
        hash.Add(fault);
        return hash.ToHashCode();
    }

    // What the check noted on a node, and why the type or ports last typed on its card here were not taken.
    private IReadOnlyList<string> NotesOf(ScriptGraphNode node)
    {
        var notes = _space.NotesOf(node);
        if (node.SignatureRefusal is not { } refusal)
            return notes;
        var all = new List<string>(notes.Count + 1) { "not taken: " + refusal };
        all.AddRange(notes);
        return all;
    }

    // Why the running script lately faulted in this node: as the host published it for a Shared script,
    // from this machine's own runtime for a Local one.
    private string? FaultOf(ScriptGraphNode node, double now)
    {
        var host = _space.Host.Target;
        if (host?.Mode.Value == ScriptMode.Local)
            return host.LocalRuntime is { } runtime && runtime.TryGetFaultMark(node.NodeId.Value ?? string.Empty, now, out var message) ? message : null;
        var fault = node.Fault.Value;
        return string.IsNullOrEmpty(fault) ? null : fault;
    }

    private void UpdateWires()
    {
        _curves.Clear();
        _keys.Clear();
        _flowWires.Clear();
        _wireMin = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        _wireMax = new float3(float.MinValue, float.MinValue, float.MinValue);
        var hash = new HashCode();
        foreach (var node in _nodes)
        {
            foreach (var wire in node.Inputs)
            {
                if (wire.Node.Target is not { IsDestroyed: false } from || !_cards.ContainsKey(from))
                    continue;
                string port = wire.Port.Value ?? string.Empty;
                if (!TryPortPoint(from, ScriptCardHandleKind.DataOutput, wire.NodePort.Value ?? string.Empty, out var start)
                    || !TryPortPoint(node, ScriptCardHandleKind.DataInput, port, out var end))
                    continue;
                var key = new ScriptWireKey(node, false, port);
                var tint = _cutMarks.Contains(key) ? ScriptEditorStyle.Cut : ScriptEditorStyle.Type(TypeOf(node, port));
                Add(key, start, end, tint, tint, ref hash);
            }
            foreach (var wire in node.Exits)
            {
                if (wire.Node.Target is not { IsDestroyed: false } to || !_cards.ContainsKey(to))
                    continue;
                string exit = wire.Port.Value ?? string.Empty;
                if (!TryPortPoint(node, ScriptCardHandleKind.FlowOutput, exit, out var start)
                    || !TryPortPoint(to, ScriptCardHandleKind.FlowInput, wire.NodePort.Value ?? string.Empty, out var end))
                    continue;
                var key = new ScriptWireKey(node, true, exit);
                _flowWires[key] = _curves.Count;
                var tint = _cutMarks.Contains(key) ? ScriptEditorStyle.Cut : ScriptEditorStyle.Flow;
                Add(key, start, end, tint, tint, ref hash);
            }
        }

        int stamp = hash.ToHashCode();
        if (stamp == _wireStamp && _wireMesh != null)
            return;
        _wireStamp = stamp;
        if (_streams.Count > 0)
            _streamsDirty = true;
        EnsureWireMesh();
        _wireMesh!.SetWires(_curves);
    }

    // Every card's slab in the graph's space, sent to the backing mesh when one of them moved, came or went.
    private void UpdateBackings()
    {
        var space = _space.Slot;
        _slabs.Clear();
        var hash = new HashCode();
        foreach (var (node, card) in _cards)
        {
            var slot = node.Slot;
            if (node.IsDestroyed || slot == null || card.Root.IsDestroyed)
                continue;
            var slab = card.SlabOf();
            float3 center;
            floatQ rotation;
            float scale;
            if (ReferenceEquals(slot.Parent, space))
            {
                var size = slot.LocalScale.Value;
                center = slot.LocalPosition.Value + slot.LocalRotation.Value * (slab.Center * size);
                rotation = slot.LocalRotation.Value;
                scale = size.x;
            }
            else
            {
                center = space.GlobalPointToLocal(slot.LocalPointToGlobal(slab.Center));
                rotation = space.GlobalRotation.Inverse * slot.GlobalRotation;
                scale = slot.GlobalScale.x / MathF.Max(1e-6f, space.GlobalScale.x);
            }
            var placed = new ScriptCardSlab(center, rotation, slab.Size * scale, slab.CornerRadius * scale);
            _slabs.Add(placed);
            hash.Add(Quantize(placed.Center));
            hash.Add(Quantize(placed.Rotation * float3.Right));
            hash.Add(Quantize(placed.Rotation * float3.Up));
            hash.Add(Quantize(new float3(placed.Size.x, placed.Size.y, placed.CornerRadius)));
        }
        int stamp = hash.ToHashCode();
        if (stamp == _slabStamp && _backings != null && _backings.SlabCount == _slabs.Count)
            return;
        _slabStamp = stamp;
        if (_backingSlot is not { IsDestroyed: false } || _backings == null)
        {
            _backingSlot = space.AddLocalSlot("Card Backings");
            _backings = _backingSlot.AttachComponent<ScriptCardBackings>();
            var renderer = _backingSlot.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = _backings;
            renderer.Material.Target = ScriptNodeCard.BackingMaterial(space.World!);
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
        }
        _backings.SetSlabs(_slabs);
    }

    private void Add(in ScriptWireKey key, float3 start, float3 end, color from, color to, ref HashCode hash)
    {
        var slot = _space.Slot;
        var a = slot.GlobalPointToLocal(start);
        var b = slot.GlobalPointToLocal(end);
        var curve = new ScriptWireCurve(a, b, from, to);
        _curves.Add(curve);
        _keys.Add(key);
        ScriptWireMesh.ControlPoints(curve, out var p0, out var p1, out var p2, out var p3);
        _wireMin = Min(_wireMin, Min(Min(p0, p1), Min(p2, p3)));
        _wireMax = Max(_wireMax, Max(Max(p0, p1), Max(p2, p3)));
        hash.Add(Quantize(a));
        hash.Add(Quantize(b));
        hash.Add(from.GetHashCode());
    }

    // The wire each pointer is on drawn over itself, thicker and brighter, with a bead where a press puts a relay.
    // Remade only when one of them moves or goes.
    private void UpdateHover()
    {
        _hoverCurves.Clear();
        _hoverBeads.Clear();
        var hash = new HashCode();
        foreach (var (_, on) in _hovers)
        {
            int index = _keys.IndexOf(on.Key);
            if (index < 0)
                continue;
            var curve = _curves[index];
            _hoverCurves.Add(curve with { FromColor = Lift(curve.FromColor), ToColor = Lift(curve.ToColor), Width = HoverWidth });
            _hoverBeads.Add((on.Point, ScriptWireMesh.Radius * 2.6f, ScriptEditorStyle.Snap));
            hash.Add(Quantize(curve.From));
            hash.Add(Quantize(curve.To));
            hash.Add(Quantize(on.Point));
            hash.Add(curve.FromColor.GetHashCode());
        }
        int stamp = _hoverCurves.Count == 0 ? 0 : hash.ToHashCode();
        if (stamp == _hoverStamp)
            return;
        _hoverStamp = stamp;
        if (_hoverCurves.Count == 0)
        {
            foreach (var slot in new[] { _hoverSlot, _markSlot })
            {
                if (slot is { IsDestroyed: false } && slot.ActiveSelf.Value)
                    slot.ActiveSelf.Value = false;
            }
            return;
        }
        if (_hoverSlot is not { IsDestroyed: false } || _hoverMesh == null)
        {
            _hoverSlot = _space.Slot.AddLocalSlot("Wire hover");
            _hoverMesh = _hoverSlot.AttachComponent<ScriptWireMesh>();
            DrawPipe(_hoverSlot, _hoverMesh, false);
        }
        if (_markSlot is not { IsDestroyed: false } || _mark == null)
        {
            _markSlot = _space.Slot.AddLocalSlot("Relay mark");
            _mark = _markSlot.AttachComponent<ScriptPulseMesh>();
            Draw(_markSlot, _mark);
        }
        _hoverSlot.ActiveSelf.Value = true;
        _markSlot.ActiveSelf.Value = true;
        _hoverMesh.SetWires(_hoverCurves);
        _mark.SetBeads(_hoverBeads);
    }

    private static color Lift(color tint) => color.Lerp(tint, new color(1f, 1f, 1f, tint.a), HoverLift);

    private static (int, int, int) Quantize(float3 p) => ((int)MathF.Round(p.x * 5000f), (int)MathF.Round(p.y * 5000f), (int)MathF.Round(p.z * 5000f));

    private static Lumora.Script.ScriptType? TypeOf(ScriptGraphNode node, string input)
    {
        var overload = node.Overload;
        int index = overload?.ConnectableInputIndex(input) ?? -1;
        return index >= 0 ? overload!.Inputs[index].Type : null;
    }

    // Counts are sampled from a running instance here, or from the host's publication on a guest.
    // Establish zero even before the first publication, otherwise the first actual execution is lost.
    private void UpdateSignals(double now)
    {
        var host = _space.Host.Target;
        var local = host?.Mode.Value == ScriptMode.Local ? host.LocalRuntime : host?.SharedRuntime;
        if (!ReferenceEquals(local, _localTracesFor))
        {
            _localTracesFor = local;
            _localTraces.Clear();
            _seenCounts.Clear();
            _signals.Clear();
            if (local != null)
            {
                var traces = local.Runner.Compiled.Traces;
                for (int i = 0; i < traces.Count; i++)
                    _localTraces[(traces[i].NodeId, traces[i].Exit)] = i;
                // This view was already watching when the new runtime started. Its first execution
                // belongs to this session, unlike historic counts when a graph is first opened.
                if (_hasSampledTraces)
                    foreach (var key in _flowWires.Keys)
                        _seenCounts[key] = 0;
            }
        }

        foreach (var (key, _) in _flowWires)
        {
            int count = local != null ? LocalCount(local, key) : host?.Mode.Value == ScriptMode.Local ? -1 : PublishedCount(key);
            if (count < 0)
            {
                _seenCounts.TryAdd(key, 0);
                continue;
            }
            if (!_seenCounts.TryGetValue(key, out int seen))
            {
                // Opening a graph with existing history is not a new execution.
                _seenCounts[key] = count;
                continue;
            }
            int fresh = System.Math.Min(count < seen ? count : count - seen, MaxBurst);
            _seenCounts[key] = count;
            if (fresh == 0)
                continue;
            if (Streams(key, now))
                continue;
            // A wire that fires every frame shows a steady train a gap apart, not a pulse a frame: one a frame was
            // hundreds of pulses on a busy graph, each its own stretch of tube made and sent every frame. -xlinka
            double next = _lastSignal.TryGetValue(key, out double last) ? System.Math.Max(now, last + SignalGap) : now;
            for (int i = 0; i < fresh && _signals.Count < MaxSignals && next - now <= SignalGap * MaxBurst; i++)
            {
                _signals.Add((key, next));
                _lastSignal[key] = next;
                next += SignalGap;
            }
        }

        _hasSampledTraces = true;
        _goneTraceKeys.Clear();
        foreach (var key in _seenCounts.Keys)
        {
            if (!_flowWires.ContainsKey(key))
                _goneTraceKeys.Add(key);
        }
        foreach (var key in _goneTraceKeys)
        {
            _seenCounts.Remove(key);
            _lastSignal.Remove(key);
            _fireRuns.Remove(key);
            if (_streams.Remove(key))
                _streamsDirty = true;
        }
        UpdateStreams(now);

        _streaks.Clear();
        for (int i = _signals.Count - 1; i >= 0; i--)
        {
            var (key, start) = _signals[i];
            double t = (now - start) / SignalSeconds;
            if (t > 1.0 || !_flowWires.TryGetValue(key, out int wire))
            {
                _signals.RemoveAt(i);
                continue;
            }
            if (t < 0.0)
                continue;
            float head = (float)t * 1.28f;
            _streaks.Add(_curves[wire] with
            {
                Start = head - 0.28f, End = head, Pulse = true,
                FromColor = ScriptEditorStyle.Signal, ToColor = ScriptEditorStyle.Signal,
            });
        }

        if (_streaks.Count == 0)
        {
            if (_signalMesh?.WireCount > 0)
                _signalMesh.SetWires(_streaks);
            return;
        }
        EnsureSignalMesh();
        _signalMesh!.SetWires(_streaks);
    }

    // Whether a wire that just fired is firing all the time. One that is shows a steady glow running down it,
    // drawn once and scrolled by its material, instead of a fresh pulse a frame: an avatar's every-frame
    // flows made hundreds of pulses, each a stretch of tube remade and sent every frame. -xlinka
    private bool Streams(in ScriptWireKey key, double now)
    {
        var (last, run) = _fireRuns.TryGetValue(key, out var seen) ? seen : (double.NegativeInfinity, 0);
        run = now - last <= StreamGap ? run + 1 : 1;
        _fireRuns[key] = (now, run);
        if (run < StreamRun)
            return false;
        if (_streams.Add(key))
            _streamsDirty = true;
        return true;
    }

    private void UpdateStreams(double now)
    {
        _streamsGone.Clear();
        foreach (var key in _streams)
        {
            if (!_flowWires.ContainsKey(key) || !_fireRuns.TryGetValue(key, out var fired) || now - fired.Last > StreamHold)
                _streamsGone.Add(key);
        }
        foreach (var key in _streamsGone)
        {
            _streams.Remove(key);
            _streamsDirty = true;
        }
        if (_streamsDirty)
        {
            _streamsDirty = false;
            _streamCurves.Clear();
            foreach (var key in _streams)
            {
                if (_flowWires.TryGetValue(key, out int wire) && wire < _curves.Count)
                    _streamCurves.Add(_curves[wire] with { FromColor = ScriptEditorStyle.Signal, ToColor = ScriptEditorStyle.Signal, Width = 1.2f });
            }
            if (_streamCurves.Count > 0 || _streamMesh != null)
            {
                EnsureStreamMesh();
                _streamMesh!.SetWires(_streamCurves);
                if (_streamSlot!.ActiveSelf.Value != _streamCurves.Count > 0)
                    _streamSlot.ActiveSelf.Value = _streamCurves.Count > 0;
            }
        }
        if (_streams.Count > 0 && _streamMaterial != null)
            _streamMaterial.MaskOffset.Value = new float2(-(float)(now * StreamSpeed / ScriptWireMesh.StreamSpacing % 1.0), 0f);
    }

    private void EnsureStreamMesh()
    {
        if (_streamSlot != null && !_streamSlot.IsDestroyed && _streamMesh != null)
            return;
        _streamSlot = _space.Slot.AddLocalSlot("Streams");
        _streamMesh = _streamSlot.AttachComponent<ScriptWireMesh>();
        _streamMesh.Lengthwise = true;
        DrawPipe(_streamSlot, _streamMesh, true);
        _streamMaterial = _streamSlot.GetComponent<FresnelMaterial>();
        // Two texels, lit and clear, sampled smoothly and repeated: a soft glow every StreamSpacing.
        var pattern = _streamSlot.AttachComponent<CheckerTextureProvider>();
        pattern.Width.Value = 2;
        pattern.Height.Value = 1;
        pattern.CellSize.Value = 1;
        pattern.ColorA.Value = new color(1f, 1f, 1f, 1f);
        pattern.ColorB.Value = new color(0f, 0f, 0f, 0f);
        pattern.GenerateMipmaps.Value = false;
        if (_streamMaterial != null)
        {
            _streamMaterial.MaskTexture.Target = pattern;
            _streamMaterial.MaskMode.Value = MaskTextureMode.MultiplyAlpha;
        }
    }

    private int LocalCount(ScriptHostRuntime runtime, in ScriptWireKey key)
        => _localTraces.TryGetValue((key.Owner.NodeId.Value ?? string.Empty, key.Port), out int index) ? runtime.ReadTrace(index) : -1;

    // What the host last published for a node's exits, read again only when the text changed.
    private int PublishedCount(in ScriptWireKey key)
    {
        var node = key.Owner;
        string text = node.Traced.Value ?? string.Empty;
        if (!_published.TryGetValue(node, out var parsed) || !ReferenceEquals(parsed.Text, text))
        {
            var counts = parsed.Counts ?? new Dictionary<string, int>(StringComparer.Ordinal);
            counts.Clear();
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.LastIndexOf(':');
                if (colon > 0 && int.TryParse(part[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    counts[part[..colon]] = value;
            }
            parsed = (text, counts);
            _published[node] = parsed;
        }
        return parsed.Counts.TryGetValue(key.Port, out int count) ? count : -1;
    }

    private void EnsureWireMesh()
    {
        if (_wireSlot != null && !_wireSlot.IsDestroyed && _wireMesh != null)
            return;
        _wireSlot = _space.Slot.AddLocalSlot("Wires");
        _wireMesh = _wireSlot.AttachComponent<ScriptWireMesh>();
        DrawPipe(_wireSlot, _wireMesh, false);
        _wireSlot.AttachComponent<ScriptWireTarget>().Space = _space;
    }

    private void EnsureSignalMesh()
    {
        if (_signalSlot != null && !_signalSlot.IsDestroyed && _signalMesh != null)
            return;
        _signalSlot = _space.Slot.AddLocalSlot("Signals");
        _signalMesh = _signalSlot.AttachComponent<ScriptWireMesh>();
        DrawPipe(_signalSlot, _signalMesh, true);
    }

    internal static void DrawPipe(Slot slot, Component mesh, bool pulse = false)
    {
        var material = slot.AttachComponent<FresnelMaterial>();
        material.UseVertexColor.Value = true;
        // Solid type colours stay legible over the world. A dark silhouette gives the tube contrast on
        // bright backgrounds and its round shape without depending on scene lights. Only flow glows blend.
        material.Exponent.Value = pulse ? 1.4f : 0.5f;
        material.NearColor.Value = pulse ? new colorHDR(1.8f, 1.8f, 1.8f, 1f) : colorHDR.White;
        material.FarColor.Value = pulse ? new colorHDR(1.3f, 1.3f, 1.3f, 0.8f) : new colorHDR(0.06f, 0.06f, 0.06f, 1f);
        material.BlendMode.Value = pulse ? BlendMode.Additive : BlendMode.Opaque;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }

    // Unlit, coloured by vertex, no shadow: how every mesh the editor draws on its own looks.
    public static void Draw(Slot slot, Component mesh)
    {
        var material = slot.AttachComponent<UnlitMaterial>();
        material.UseVertexColor.Value = true;
        material.TintColor.Value = colorHDR.White;
        var renderer = slot.AttachComponent<MeshRenderer>();
        renderer.Mesh.Target = mesh;
        renderer.Material.Target = material;
        renderer.ShadowCastMode.Value = ShadowCastMode.Off;
    }
}
