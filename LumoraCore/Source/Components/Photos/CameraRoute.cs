// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core.Assets;
using Lumora.Core.Components.Assets;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// A camera's glide through anchors, in order. The lens runs one smooth curve through every stop (centripetal
// Catmull-Rom: close stops don't loop or overshoot), turns through the stops' rotations with no kink at any
// stop and takes each stop's width of view along. The travel time is shared out by how far each leg goes and how
// much it turns, so the whole route keeps one pace. A stop with a hold brings the camera to rest there for that
// long; with EaseEnds it also starts and finishes from rest. The path is drawn for eyes only, like the anchors'
// markers, so no camera films it. -xlinka
[ComponentCategory("Interaction/Tools")]
public class CameraRoute : Component
{
    public readonly SyncRefList<CameraAnchor> Stops = new();
    // Seconds on the move from the first stop to the last; holds come on top.
    public readonly Sync<float> Duration = new(12f);
    public readonly Sync<bool> Loop = new();
    public readonly Sync<bool> EaseEnds = new(true);
    public readonly Sync<bool> ShowPath = new(true);

    // How long a start or a stop takes to ease, at most.
    public const float EaseSeconds = 1.6f;
    // A turn or a zoom costs travel time too, or a pan on the spot would whip round in no time.
    private const float TurnMetres = 0.6f;
    private const float ZoomMetresPerDegree = 0.02f;
    private const int SamplesPerLeg = 48;
    private const float PathRadius = 0.006f;
    private static readonly colorHDR PathTint = new(0.95f, 0.7f, 0.1f, 0.45f);

    private static readonly ConditionalWeakTable<World, List<CameraRoute>> Registry = new();

    private readonly List<float3> _points = new();
    private readonly List<floatQ> _turns = new();
    private readonly List<float> _fovs = new();
    private readonly List<float> _holds = new();
    private readonly List<float3> _bezier = new();
    private readonly List<float> _knots = new();
    private readonly List<float3> _pointSlopes = new();
    private readonly List<floatQ> _turnSlopes = new();
    private readonly List<float> _fovSlopes = new();
    private readonly List<Run> _runs = new();
    private float[] _effort = Array.Empty<float>();
    private int _legs;
    private bool _closed;
    private double _length;
    private int _signature;
    private bool _built;

    private Slot? _path;
    private readonly List<BezierTubeMesh> _tubes = new();
    private int _pathSignature;

    private struct Run
    {
        public double Start;
        public float Travel;
        public float From;
        public float To;
        public float EaseIn;
        public float EaseOut;
        public float HoldAfter;
    }

    public static IReadOnlyList<CameraRoute> InWorld(World? world)
        => world != null && Registry.TryGetValue(world, out var list) ? list : Array.Empty<CameraRoute>();

    // Two stops make a route; fewer is somewhere to sit, not a glide.
    public bool HasPath
    {
        get
        {
            Build();
            return _points.Count >= 2;
        }
    }

    // Seconds from the start to the end, holds included; one lap when it loops.
    public double Length
    {
        get
        {
            Build();
            return _length;
        }
    }

    public int StopCount
    {
        get
        {
            int count = 0;
            foreach (var stop in Stops)
            {
                if (stop is { IsDestroyed: false })
                    count++;
            }
            return count;
        }
    }

    public static CameraRoute? Spawn(World world, IReadOnlyList<CameraAnchor> stops, float duration)
    {
        if (world.RootSlot == null || stops.Count == 0)
            return null;
        using var own = world.EnterSpawnAllocation();
        var slot = world.RootSlot.AddSlot("Camera Route");
        slot.GlobalPosition = stops[0].CameraRoot.GlobalPosition;
        var route = slot.AttachComponent<CameraRoute>();
        route.Duration.Value = duration;
        foreach (var stop in stops)
            route.Stops.Add(stop);
        return route;
    }

    // A pace a person can follow, for a route nobody has timed yet.
    public static float SuggestedDuration(IReadOnlyList<CameraAnchor> stops)
    {
        float metres = 0f;
        for (int i = 1; i < stops.Count; i++)
            metres += (stops[i].CameraRoot.GlobalPosition - stops[i - 1].CameraRoot.GlobalPosition).Length;
        return MathF.Max(4f, metres / 0.8f);
    }

    public override void OnStart()
    {
        base.OnStart();
        if (World != null)
        {
            var list = Registry.GetOrCreateValue(World);
            if (!list.Contains(this))
                list.Add(this);
        }
    }

    public override void OnDestroy()
    {
        if (World != null && Registry.TryGetValue(World, out var list))
            list.Remove(this);
        if (_path is { IsDestroyed: false })
            _path.Destroy();
        _path = null;
        _tubes.Clear();
        base.OnDestroy();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        Build();
        UpdatePath();
    }

    // Where a lens on the route is this many seconds after it set off. False once a route that does not loop
    // has run out; the pose is then its last stop's.
    public bool Sample(double seconds, out float3 position, out floatQ rotation, out float fieldOfView)
    {
        Build();
        position = float3.Zero;
        rotation = floatQ.Identity;
        fieldOfView = 60f;
        if (_points.Count == 0)
            return false;
        if (_points.Count == 1 || _runs.Count == 0)
        {
            position = _points[0];
            rotation = _turns[0];
            fieldOfView = _fovs[0];
            return false;
        }

        bool running = true;
        if (Loop.Value && _length > 1e-6)
        {
            seconds %= _length;
            if (seconds < 0)
                seconds += _length;
        }
        else if (seconds >= _length)
        {
            running = false;
            seconds = _length;
        }

        float effort = _runs[0].From;
        if (seconds > 0)
        {
            effort = _runs[^1].To;
            foreach (var run in _runs)
            {
                if (seconds < run.Start)
                {
                    effort = run.From;
                    break;
                }
                double into = seconds - run.Start;
                if (into <= run.Travel)
                {
                    effort = run.From + Covered((float)into, run);
                    break;
                }
                if (into <= run.Travel + run.HoldAfter)
                {
                    effort = run.To;
                    break;
                }
            }
        }
        At(effort, out position, out rotation, out fieldOfView);
        return running;
    }

    // THE CURVE

    private void Build()
    {
        int signature = Signature();
        if (_built && signature == _signature)
            return;
        _built = true;
        _signature = signature;

        _points.Clear();
        _turns.Clear();
        _fovs.Clear();
        _holds.Clear();
        foreach (var stop in Stops)
        {
            if (stop is not { IsDestroyed: false })
                continue;
            var root = stop.CameraRoot;
            var turn = root.GlobalRotation;
            // Neighbours on the same side of the sphere, so the blend between them never takes the long way.
            if (_turns.Count > 0 && floatQ.Dot(_turns[^1], turn) < 0f)
                turn = new floatQ(-turn.x, -turn.y, -turn.z, -turn.w);
            _points.Add(root.GlobalPosition);
            _turns.Add(turn);
            _fovs.Add(System.Math.Clamp(stop.FieldOfView.Value, 1f, 170f));
            _holds.Add(MathF.Max(0f, stop.Hold.Value));
        }
        bool closed = Loop.Value && _points.Count >= 3;
        _closed = closed;
        if (closed)
        {
            var first = _turns[0];
            if (floatQ.Dot(_turns[^1], first) < 0f)
                first = new floatQ(-first.x, -first.y, -first.z, -first.w);
            _points.Add(_points[0]);
            _turns.Add(first);
            _fovs.Add(_fovs[0]);
            _holds.Add(_holds[0]);
        }
        _legs = System.Math.Max(0, _points.Count - 1);
        _bezier.Clear();
        _runs.Clear();
        _length = 0;
        if (_legs == 0)
        {
            _effort = Array.Empty<float>();
            return;
        }

        Shape(closed);

        _effort = new float[_legs * SamplesPerLeg + 1];
        float total = 0f;
        Pose(0, 0f, out var lastPosition, out var lastTurn, out var lastFov);
        for (int i = 0; i < _legs; i++)
        {
            for (int s = 1; s <= SamplesPerLeg; s++)
            {
                Pose(i, s / (float)SamplesPerLeg, out var position, out var turn, out var fov);
                total += (position - lastPosition).Length + Angle(lastTurn, turn) * TurnMetres + MathF.Abs(fov - lastFov) * ZoomMetresPerDegree;
                _effort[i * SamplesPerLeg + s] = total;
                lastPosition = position;
                lastTurn = turn;
                lastFov = fov;
            }
        }

        // Rests split the route into runs; each run's share of the time is its share of the effort.
        int count = _points.Count;
        bool ends = EaseEnds.Value && !closed;
        bool Rests(int stop) => _holds[stop] > 0f || (ends && (stop == 0 || stop == count - 1));
        float pace = total > 1e-5f ? MathF.Max(0.1f, Duration.Value) / total : 0f;
        double clock = closed ? 0 : _holds[0];
        int from = 0;
        for (int stop = 1; stop < count; stop++)
        {
            if (stop < count - 1 && !Rests(stop))
                continue;
            float a = _effort[from * SamplesPerLeg];
            float b = _effort[stop * SamplesPerLeg];
            float travel = MathF.Max(1e-3f, (b - a) * pace);
            float easeIn = Rests(from) ? MathF.Min(EaseSeconds, travel * 0.5f) : 0f;
            float easeOut = Rests(stop) ? MathF.Min(EaseSeconds, travel * 0.5f) : 0f;
            // A loop's last stop is its first: that hold waits at the top of the next lap instead.
            float hold = closed && stop == count - 1 ? _holds[0] : _holds[stop];
            _runs.Add(new Run { Start = clock, Travel = travel, From = a, To = b, EaseIn = easeIn, EaseOut = easeOut, HoldAfter = hold });
            clock += travel + hold;
            from = stop;
        }
        _length = clock;
    }

    private int Signature()
    {
        var hash = new HashCode();
        hash.Add(Duration.Value);
        hash.Add(Loop.Value);
        hash.Add(EaseEnds.Value);
        foreach (var stop in Stops)
        {
            if (stop is not { IsDestroyed: false })
                continue;
            var root = stop.CameraRoot;
            hash.Add(root.GlobalPosition);
            hash.Add(root.GlobalRotation);
            hash.Add(stop.FieldOfView.Value);
            hash.Add(stop.Hold.Value);
        }
        return hash.ToHashCode();
    }

    // Every channel (where the lens is, where it looks, how wide) is one centripetal Catmull-Rom over the same knots,
    // spaced by the root of how far and how much each leg moves and turns. Sharing the knots keeps the speed and the
    // turn rate continuous through every stop; a separate spacing per channel put a jolt at each anchor. The legs
    // are kept as bezier handles too, so the drawn path is the same curve.
    private void Shape(bool closed)
    {
        int count = _points.Count;
        _knots.Clear();
        for (int i = 0; i < _legs; i++)
        {
            float span = (_points[i + 1] - _points[i]).Length + Angle(_turns[i], _turns[i + 1]) * TurnMetres
                + MathF.Abs(_fovs[i + 1] - _fovs[i]) * ZoomMetresPerDegree;
            _knots.Add(MathF.Max(1e-3f, MathF.Sqrt(span)));
        }
        _pointSlopes.Clear();
        _turnSlopes.Clear();
        _fovSlopes.Clear();
        for (int k = 0; k < count; k++)
        {
            Around(k, closed, out int before, out int after, out float dBefore, out float dAfter);
            var p = _points[k];
            var pb = before >= 0 ? _points[before] : p * 2f - _points[after];
            var pa = after >= 0 ? _points[after] : p * 2f - _points[before];
            _pointSlopes.Add((p - pb) / dBefore - (pa - pb) / (dBefore + dAfter) + (pa - p) / dAfter);

            var q = _turns[k];
            var qb = before >= 0 ? Side(_turns[before], q) : Mirror(Side(_turns[after], q), q);
            var qa = after >= 0 ? Side(_turns[after], q) : Mirror(Side(_turns[before], q), q);
            _turnSlopes.Add(new floatQ(
                Slope(qb.x, q.x, qa.x, dBefore, dAfter), Slope(qb.y, q.y, qa.y, dBefore, dAfter),
                Slope(qb.z, q.z, qa.z, dBefore, dAfter), Slope(qb.w, q.w, qa.w, dBefore, dAfter)));

            float f = _fovs[k];
            float fb = before >= 0 ? _fovs[before] : 2f * f - _fovs[after];
            float fa = after >= 0 ? _fovs[after] : 2f * f - _fovs[before];
            _fovSlopes.Add(Slope(fb, f, fa, dBefore, dAfter));
        }
        for (int i = 0; i < _legs; i++)
        {
            float d = _knots[i];
            _bezier.Add(_points[i]);
            _bezier.Add(_points[i] + _pointSlopes[i] * (d / 3f));
            _bezier.Add(_points[i + 1] - _pointSlopes[i + 1] * (d / 3f));
            _bezier.Add(_points[i + 1]);
        }
    }

    // The stops either side of stop k and the knot spans to them; -1 past the end of an open route, where the
    // curve carries straight on.
    private void Around(int k, bool closed, out int before, out int after, out float dBefore, out float dAfter)
    {
        int count = _points.Count;
        if (closed)
        {
            int unique = count - 1;
            int at = k % unique;
            before = (at - 1 + unique) % unique;
            after = (at + 1) % unique;
            dBefore = _knots[(at - 1 + _legs) % _legs];
            dAfter = _knots[at % _legs];
            return;
        }
        before = k > 0 ? k - 1 : -1;
        after = k < count - 1 ? k + 1 : -1;
        dBefore = k > 0 ? _knots[k - 1] : _knots[0];
        dAfter = k < count - 1 ? _knots[k] : _knots[_legs - 1];
    }

    private static float Slope(float before, float at, float after, float dBefore, float dAfter)
        => (at - before) / dBefore - (after - before) / (dBefore + dAfter) + (after - at) / dAfter;

    private static floatQ Side(floatQ q, floatQ near) => floatQ.Dot(q, near) < 0f ? new floatQ(-q.x, -q.y, -q.z, -q.w) : q;

    private static floatQ Mirror(floatQ q, floatQ about)
        => new floatQ(2f * about.x - q.x, 2f * about.y - q.y, 2f * about.z - q.z, 2f * about.w - q.w);

    private static float Angle(floatQ a, floatQ b) => 2f * MathF.Acos(System.Math.Clamp(MathF.Abs(floatQ.Dot(a, b)), 0f, 1f));

    private void Pose(int leg, float t, out float3 position, out floatQ rotation, out float fov)
    {
        float t2 = t * t, t3 = t2 * t;
        float h00 = 2f * t3 - 3f * t2 + 1f, h10 = t3 - 2f * t2 + t, h01 = -2f * t3 + 3f * t2, h11 = t3 - t2;
        float d = _knots[leg];
        position = _points[leg] * h00 + _pointSlopes[leg] * (h10 * d) + _points[leg + 1] * h01 + _pointSlopes[leg + 1] * (h11 * d);

        var q0 = _turns[leg];
        var q1 = Side(_turns[leg + 1], q0);
        var m0 = _turnSlopes[leg];
        var m1 = floatQ.Dot(_turns[leg + 1], q0) < 0f ? new floatQ(-_turnSlopes[leg + 1].x, -_turnSlopes[leg + 1].y, -_turnSlopes[leg + 1].z, -_turnSlopes[leg + 1].w) : _turnSlopes[leg + 1];
        rotation = new floatQ(
            q0.x * h00 + m0.x * h10 * d + q1.x * h01 + m1.x * h11 * d,
            q0.y * h00 + m0.y * h10 * d + q1.y * h01 + m1.y * h11 * d,
            q0.z * h00 + m0.z * h10 * d + q1.z * h01 + m1.z * h11 * d,
            q0.w * h00 + m0.w * h10 * d + q1.w * h01 + m1.w * h11 * d).Normalized;
        fov = _fovs[leg] * h00 + _fovSlopes[leg] * h10 * d + _fovs[leg + 1] * h01 + _fovSlopes[leg + 1] * h11 * d;
    }

    private void At(float effort, out float3 position, out floatQ rotation, out float fov)
    {
        int last = _effort.Length - 1;
        if (effort <= 0f)
        {
            Pose(0, 0f, out position, out rotation, out fov);
            return;
        }
        if (effort >= _effort[last])
        {
            Pose(_legs - 1, 1f, out position, out rotation, out fov);
            return;
        }
        int lo = 0;
        int hi = last;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_effort[mid] <= effort)
                lo = mid;
            else
                hi = mid;
        }
        float span = _effort[hi] - _effort[lo];
        float f = span > 1e-7f ? (effort - _effort[lo]) / span : 0f;
        float along = (lo + f) / SamplesPerLeg;
        int leg = System.Math.Min(_legs - 1, (int)along);
        Pose(leg, along - leg, out position, out rotation, out fov);
    }

    // How far into a run (in effort) after this many seconds: steady in the middle, and from or to rest along
    // a half cosine of speed at an eased end, so the pull away and the stop carry no jolt.
    private static float Covered(float t, in Run run)
    {
        float length = run.To - run.From;
        float travel = run.Travel;
        if (t <= 0f)
            return 0f;
        if (t >= travel)
            return length;
        float speed = length / MathF.Max(1e-5f, travel - 0.5f * (run.EaseIn + run.EaseOut));
        if (run.EaseIn > 0f && t < run.EaseIn)
            return speed * (t * 0.5f - run.EaseIn / (2f * MathF.PI) * MathF.Sin(MathF.PI * t / run.EaseIn));
        float left = travel - t;
        if (run.EaseOut > 0f && left < run.EaseOut)
            return length - speed * (left * 0.5f - run.EaseOut / (2f * MathF.PI) * MathF.Sin(MathF.PI * left / run.EaseOut));
        return speed * (run.EaseIn * 0.5f + (t - run.EaseIn));
    }

    // THE DRAWN PATH
    //
    // Built on every machine for its own eyes, one bezier tube a leg, at the world root so it sits on the
    // anchors wherever this slot was left.

    private void UpdatePath()
    {
        bool want = ShowPath.Value && _legs > 0 && Enabled;
        if (!want)
        {
            if (_path is { IsDestroyed: false })
                _path.Destroy();
            _path = null;
            _tubes.Clear();
            return;
        }
        if (_path is { IsDestroyed: false } && _pathSignature == _signature)
            return;
        _pathSignature = _signature;
        // A dragged anchor only bends the legs; the tubes are remade when the count of legs changes.
        if (_path is not { IsDestroyed: false } || _tubes.Count != _legs)
        {
            if (_path is { IsDestroyed: false })
                _path.Destroy();
            _tubes.Clear();
            var root = World?.RootSlot;
            if (root == null)
                return;
            _path = root.AddLocalSlot("Camera Route Path");
            _path.AttachComponent<RenderLayerOverride>().Layer.Value = RenderLayerOverride.EyesOnlyLayer;
            var material = _path.AttachComponent<UnlitMaterial>();
            material.TintColor.Value = PathTint;
            material.BlendMode.Value = BlendMode.Alpha;
            for (int i = 0; i < _legs; i++)
            {
                var leg = _path.AddLocalSlot("Leg");
                var tube = leg.AttachComponent<BezierTubeMesh>();
                tube.Radius.Value = PathRadius;
                tube.Sides.Value = 6;
                tube.Samples.Value = 24;
                var renderer = leg.AttachComponent<MeshRenderer>();
                renderer.Mesh.Target = tube;
                renderer.Material.Target = material;
                _tubes.Add(tube);
            }
        }
        for (int i = 0; i < _tubes.Count; i++)
        {
            int b = i * 4;
            var tube = _tubes[i];
            Put(tube.Point0, _bezier[b]);
            Put(tube.Point1, _bezier[b + 1]);
            Put(tube.Point2, _bezier[b + 2]);
            Put(tube.Point3, _bezier[b + 3]);
        }
    }

    private static void Put(Sync<float3> field, float3 value)
    {
        if (field.Value != value)
            field.Value = value;
    }
}
