// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Simulation.Particles;

namespace Lumora.Core.Components.Meshes;

// Turns a brush stroke's control points into its mesh, and keeps what it made.
//
// A stroke only ever grows at its end, and a Catmull-Rom segment only looks two points either side of
// itself, so a new point changes the last couple of segments and nothing before them. The rings already
// laid down stay in the mesh buffers; a build cuts the buffers back to where the change reaches and lays
// the rest again. A stroke being drawn costs the same per frame at a hundred points as at ten thousand,
// apart from the copy up to the GPU. -xlinka
public sealed class StrokeGeometry
{
    private const int CapRings = 3;
    private const float RibbonTaper = 2f;

    private struct Ring
    {
        public float3 Position;
        public float3 Tangent;
        // tube: the transported frame axis the profile starts from; ribbon: the face normal
        public float3 Normal;
        public float3 Side;
        public float Width;
        public float Arc;
        // where this ring's emission begins, the start cap included for the first ring
        public int VertexStart;
        public int MainTris;
        public int GlowTris;
        public int RingVertex;
        public int GlowVertex;
    }

    private readonly List<Ring> _rings = new();
    private readonly List<int> _blockVertex = new();
    private readonly List<int> _blockTris = new();

    private float3[] _control = new float3[32];
    private float[] _widths = new float[32];
    private float3[] _facing = new float3[32];
    private int _count;
    private bool _hasFacing;

    private BrushTip _tip = (BrushTip)(-1);
    private colorHDR _tint;
    private int _seed;
    private int _builtCount;
    private int _endTaperFrom = int.MaxValue;
    private int _clearCount = -1;
    private int _vertexCount = -1;

    private PhosTriangleSubmesh? _main;
    private PhosTriangleSubmesh? _glow;

    private readonly List<float2> _flat = new();
    private readonly List<int> _poly = new();

    public int TriangleCount => (_main?.Count ?? 0) + (_glow?.Count ?? 0);

    public void Reset()
    {
        _rings.Clear();
        _blockVertex.Clear();
        _blockTris.Clear();
        _builtCount = 0;
        _tip = (BrushTip)(-1);
        _main = null;
        _glow = null;
        _clearCount = -1;
        _vertexCount = -1;
        _endTaperFrom = int.MaxValue;
    }

    // points: xyz position, w width, both in the stroke's own space. facing may be empty for the round tips.
    // firstChanged is the first point that differs from the last build (0 for everything).
    public void Build(PhosMesh mesh, BrushTip tip, colorHDR tint, int seed, IReadOnlyList<float4> points,
        IReadOnlyList<float3> facing, int firstChanged)
    {
        bool fresh = tip != _tip || seed != _seed || !tint.Equals(_tint) || mesh.ClearCount != _clearCount
            || mesh.VertexCount != _vertexCount || _main == null || !mesh.Submeshes.Contains(_main);
        if (fresh)
            firstChanged = 0;
        firstChanged = System.Math.Clamp(firstChanged, 0, System.Math.Min(points.Count, _builtCount));

        Load(points, facing, firstChanged);

        if (fresh)
            Setup(mesh, tip);
        _tip = tip;
        _tint = tint;
        _seed = seed;

        switch (tip)
        {
            case BrushTip.Confetti:
                BuildConfetti(mesh, firstChanged);
                break;
            case BrushTip.Fill:
                BuildFill(mesh);
                break;
            case BrushTip.Eraser:
                break;
            default:
                BuildLine(mesh, firstChanged);
                break;
        }

        _builtCount = _count;
        _clearCount = mesh.ClearCount;
        _vertexCount = mesh.VertexCount;
    }

    private void Load(IReadOnlyList<float4> points, IReadOnlyList<float3> facing, int from)
    {
        int count = points.Count;
        if (_control.Length < count)
        {
            int size = System.Math.Max(count, _control.Length * 2);
            Array.Resize(ref _control, size);
            Array.Resize(ref _widths, size);
            Array.Resize(ref _facing, size);
        }
        _hasFacing = facing.Count >= count && count > 0;
        for (int i = from; i < count; i++)
        {
            var p = points[i];
            _control[i] = p.xyz;
            _widths[i] = MathF.Max(p.w, 0f);
            _facing[i] = _hasFacing ? facing[i] : float3.Zero;
        }
        _count = count;
    }

    private void Setup(PhosMesh mesh, BrushTip tip)
    {
        mesh.Clear();
        mesh.HasNormals = true;
        mesh.HasColors = tip == BrushTip.Confetti;
        mesh.SetHasUV(0, true);
        _main = new PhosTriangleSubmesh(mesh);
        mesh.Submeshes.Add(_main);
        _glow = null;
        if (tip == BrushTip.Neon)
        {
            _glow = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(_glow);
        }
        _rings.Clear();
        _blockVertex.Clear();
        _blockTris.Clear();
        _builtCount = 0;
        _endTaperFrom = int.MaxValue;
    }

    // Points come in about half a width apart, so two rings a segment is already a ring every quarter width;
    // the spline's points are far apart on purpose and the curve between them is the whole point of it.
    private static int Subdivisions(BrushTip tip) => tip == BrushTip.Spline ? 6 : 2;

    private static int Sides(BrushTip tip) => tip switch
    {
        BrushTip.Tube => 8,
        BrushTip.Spline => 8,
        BrushTip.Neon => 6,
        _ => 0
    };

    private const int GlowSides = 8;

    // Core and halo radius as a share of the stroke's width. The halo is the width you asked for, the core a
    // bright thread down its middle.
    private const float NeonCore = 0.2f;
    private const float NeonHalo = 0.5f;

    private void Truncate(PhosMesh mesh, int vertex, int mainTris, int glowTris)
    {
        if (mesh.VertexCount > vertex)
            mesh.RemoveVertices(vertex, mesh.VertexCount - vertex, updateSubmeshes: false);
        if (_main != null && _main.Count > mainTris)
            _main.SetCount(mainTris);
        if (_glow != null && _glow.Count > glowTris)
            _glow.SetCount(glowTris);
    }

    // LINES (ribbon, tube, neon, spline)

    private void BuildLine(PhosMesh mesh, int firstChanged)
    {
        int sub = Subdivisions(_tip);
        int ringCount = _count <= 0 ? 0 : (_count - 1) * sub + 1;

        // Segment s reads points s-1 to s+2. A ring's tangent reads the rings either side of it.
        int firstSegment = System.Math.Max(0, firstChanged - 2);
        int from = System.Math.Max(0, firstSegment * sub - 1);
        if (_tip == BrushTip.Ribbon)
            from = System.Math.Min(from, _endTaperFrom);
        from = System.Math.Min(from, _rings.Count);

        if (from < _rings.Count)
        {
            var at = _rings[from];
            Truncate(mesh, at.VertexStart, at.MainTris, at.GlowTris);
            _rings.RemoveRange(from, _rings.Count - from);
        }
        else
        {
            // Nothing kept is affected, but the end cap after the last ring always goes.
            CutEndCap(mesh);
        }

        if (ringCount == 0)
        {
            Truncate(mesh, 0, 0, 0);
            _rings.Clear();
            return;
        }

        int geometryFrom = _rings.Count;
        var span = new ReadOnlySpan<float3>(_control, 0, _count);
        for (int r = geometryFrom; r < ringCount; r++)
        {
            int segment = System.Math.Min(r / sub, System.Math.Max(0, _count - 2));
            float t = _count < 2 ? 0f : (r - segment * sub) / (float)sub;
            if (r == ringCount - 1 && _count >= 2)
            {
                segment = _count - 2;
                t = 1f;
            }
            var curve = new CatmullRomSegment(span, segment);
            var ring = new Ring
            {
                Position = curve.Evaluate(t),
                Width = _count < 2 ? _widths[0] : _widths[segment] + (_widths[segment + 1] - _widths[segment]) * t,
            };
            if (_hasFacing)
            {
                var f = _count < 2 ? _facing[0] : float3.Lerp(_facing[segment], _facing[segment + 1], t);
                ring.Normal = f;
            }
            _rings.Add(ring);
        }

        // The cut already took the ring before the first moved one, whose tangent reads it; anything earlier
        // keeps the frame it was emitted with, and the transport carries on from there.
        int frameFrom = geometryFrom;
        for (int r = frameFrom; r < ringCount; r++)
        {
            var ring = _rings[r];
            float3 d;
            if (ringCount == 1)
                d = float3.Zero;
            else if (r == 0)
                d = _rings[1].Position - ring.Position;
            else if (r == ringCount - 1)
                d = ring.Position - _rings[r - 1].Position;
            else
                d = _rings[r + 1].Position - _rings[r - 1].Position;
            if (d.LengthSquared < 1e-14f)
                d = r > 0 ? _rings[r - 1].Tangent : Seed(float3.Zero);
            ring.Tangent = d.Normalized;
            ring.Arc = r == 0 ? 0f : _rings[r - 1].Arc + float3.Distance(_rings[r - 1].Position, ring.Position);
            _rings[r] = ring;
        }

        for (int r = frameFrom; r < ringCount; r++)
            _rings[r] = _tip == BrushTip.Ribbon ? RibbonFrame(r) : TubeFrame(r);

        if (_tip == BrushTip.Ribbon)
        {
            float total = _rings[ringCount - 1].Arc;
            _endTaperFrom = ringCount;
            for (int r = ringCount - 1; r >= 0; r--)
            {
                if (total - _rings[r].Arc >= RibbonTaper * MathF.Max(_rings[r].Width, 1e-5f))
                    break;
                _endTaperFrom = r;
            }
        }

        for (int r = geometryFrom; r < ringCount; r++)
            EmitRing(mesh, r, ringCount);
        EmitEndCap(mesh, ringCount);
    }

    private static float3 Seed(float3 d)
    {
        if (d.LengthSquared < 1e-12f)
            return float3.Forward;
        return d.Normalized;
    }

    private Ring TubeFrame(int r)
    {
        var ring = _rings[r];
        var t = ring.Tangent;
        float3 x;
        if (r == 0)
        {
            float3 seed = MathF.Abs(t.y) < 0.9f ? float3.Up : float3.Right;
            x = float3.Cross(seed, t).Normalized;
        }
        else
        {
            var prev = _rings[r - 1];
            x = prev.Normal - t * float3.Dot(prev.Normal, t);
            if (x.LengthSquared < 1e-10f)
            {
                float3 seed = MathF.Abs(t.y) < 0.9f ? float3.Up : float3.Right;
                x = float3.Cross(seed, t);
            }
            x = x.Normalized;
        }
        ring.Normal = x;
        ring.Side = float3.Cross(x, t);
        return ring;
    }

    // The face looks back along the brush; across the face is whatever is square to that and to the path. A
    // path running straight along the brush has no across, so it keeps the last one.
    private Ring RibbonFrame(int r)
    {
        var ring = _rings[r];
        var t = ring.Tangent;
        var facing = _hasFacing ? ring.Normal : float3.Zero;
        float3 n = facing - t * float3.Dot(facing, t);
        float3 side;
        if (n.LengthSquared > 1e-8f)
        {
            n = n.Normalized;
            side = float3.Cross(n, t);
        }
        else if (r > 0)
        {
            side = _rings[r - 1].Side;
            side = (side - t * float3.Dot(side, t));
            if (side.LengthSquared < 1e-10f)
                side = float3.Cross(MathF.Abs(t.y) < 0.9f ? float3.Up : float3.Right, t);
            side = side.Normalized;
            n = float3.Cross(t, side);
        }
        else
        {
            side = float3.Cross(MathF.Abs(t.y) < 0.9f ? float3.Up : float3.Right, t).Normalized;
            n = float3.Cross(t, side);
        }
        ring.Side = side.Normalized;
        ring.Normal = n.Normalized;
        return ring;
    }

    private float RibbonWidth(int r)
    {
        var ring = _rings[r];
        int last = _rings.Count - 1;
        float w = MathF.Max(ring.Width, 1e-5f);
        float total = _rings[last].Arc;
        float start = Smooth(ring.Arc / (RibbonTaper * w));
        float end = Smooth((total - ring.Arc) / (RibbonTaper * w));
        return ring.Width * MathF.Max(MathF.Min(start, end), 0.06f);
    }

    private static float Smooth(float x)
    {
        x = System.Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private void EmitRing(PhosMesh mesh, int r, int ringCount)
    {
        var ring = _rings[r];
        ring.VertexStart = mesh.VertexCount;
        ring.MainTris = _main!.Count;
        ring.GlowTris = _glow?.Count ?? 0;

        switch (_tip)
        {
            case BrushTip.Ribbon:
            {
                ring.RingVertex = mesh.VertexCount;
                float half = RibbonWidth(r) * 0.5f;
                int v = AddVertices(mesh, 2);
                float v0 = ring.Arc / MathF.Max(ring.Width, 1e-4f);
                SetVertex(mesh, v, ring.Position - ring.Side * half, ring.Normal, new float2(0f, v0));
                SetVertex(mesh, v + 1, ring.Position + ring.Side * half, ring.Normal, new float2(1f, v0));
                if (r > 0)
                    Quad(_main, _rings[r - 1].RingVertex, v, 1);
                break;
            }
            case BrushTip.Neon:
            {
                float core = ring.Width * NeonCore;
                float halo = ring.Width * NeonHalo;
                if (r == 0)
                {
                    StartCap(mesh, _main, ring, core, Sides(_tip));
                    ring.RingVertex = EmitProfile(mesh, ring, core, Sides(_tip));
                    StartCap(mesh, _glow!, ring, halo, GlowSides);
                    ring.GlowVertex = EmitProfile(mesh, ring, halo, GlowSides);
                }
                else
                {
                    ring.RingVertex = EmitProfile(mesh, ring, core, Sides(_tip));
                    ring.GlowVertex = EmitProfile(mesh, ring, halo, GlowSides);
                    Quad(_main, _rings[r - 1].RingVertex, ring.RingVertex, Sides(_tip));
                    Quad(_glow!, _rings[r - 1].GlowVertex, ring.GlowVertex, GlowSides);
                }
                break;
            }
            default:
            {
                float radius = ring.Width * 0.5f;
                if (r == 0)
                    StartCap(mesh, _main, ring, radius, Sides(_tip));
                ring.RingVertex = EmitProfile(mesh, ring, radius, Sides(_tip));
                if (r > 0)
                    Quad(_main, _rings[r - 1].RingVertex, ring.RingVertex, Sides(_tip));
                break;
            }
        }
        _rings[r] = ring;
    }

    // What the mesh held just before the end cap went on: everything past it is the cap.
    private int _capVertex, _capMainTris, _capGlowTris;

    private void CutEndCap(PhosMesh mesh)
    {
        if (_rings.Count == 0)
            return;
        Truncate(mesh, _capVertex, _capMainTris, _capGlowTris);
    }

    private void EmitEndCap(PhosMesh mesh, int ringCount)
    {
        _capVertex = mesh.VertexCount;
        _capMainTris = _main?.Count ?? 0;
        _capGlowTris = _glow?.Count ?? 0;
        if (_tip == BrushTip.Ribbon || ringCount == 0)
            return;
        var last = _rings[ringCount - 1];
        if (_tip == BrushTip.Neon)
        {
            EndCap(mesh, _main!, last, last.Width * NeonCore, Sides(_tip), last.RingVertex);
            EndCap(mesh, _glow!, last, last.Width * NeonHalo, GlowSides, last.GlowVertex);
            return;
        }
        EndCap(mesh, _main!, last, last.Width * 0.5f, Sides(_tip), last.RingVertex);
    }

    private int EmitProfile(PhosMesh mesh, in Ring ring, float radius, int sides)
    {
        int v = AddVertices(mesh, sides + 1);
        float vCoord = ring.Arc / MathF.Max(MathF.PI * radius * 2f, 1e-4f);
        for (int k = 0; k <= sides; k++)
        {
            float a = k / (float)sides * MathF.PI * 2f;
            float3 dir = ring.Normal * MathF.Cos(a) + ring.Side * MathF.Sin(a);
            SetVertex(mesh, v + k, ring.Position + dir * radius, dir, new float2(k / (float)sides, vCoord));
        }
        return v;
    }

    // A half sphere behind the first ring: a pole, then rings opening out to the stroke's radius. Its last ring
    // is the stroke's own first profile, emitted next.
    private void StartCap(PhosMesh mesh, PhosTriangleSubmesh submesh, in Ring ring, float radius, int sides)
    {
        int pole = AddVertices(mesh, 1);
        SetVertex(mesh, pole, ring.Position - ring.Tangent * radius, -ring.Tangent, new float2(0.5f, 0f));
        int previous = -1;
        for (int j = 1; j < CapRings; j++)
        {
            float phi = MathF.PI * 0.5f * (1f - j / (float)CapRings);
            int v = CapProfile(mesh, ring, radius, sides, phi, -1f);
            if (previous < 0)
                Fan(submesh, pole, v, sides, reverse: true);
            else
                Quad(submesh, previous, v, sides);
            previous = v;
        }
        // The body's first ring is emitted straight after this, so its vertex numbers are already known.
        Quad(submesh, previous, mesh.VertexCount, sides);
    }

    private void EndCap(PhosMesh mesh, PhosTriangleSubmesh submesh, in Ring ring, float radius, int sides, int bodyRing)
    {
        int previous = bodyRing;
        for (int j = 1; j < CapRings; j++)
        {
            float phi = MathF.PI * 0.5f * (j / (float)CapRings);
            int v = CapProfile(mesh, ring, radius, sides, phi, 1f);
            Quad(submesh, previous, v, sides);
            previous = v;
        }
        int pole = AddVertices(mesh, 1);
        SetVertex(mesh, pole, ring.Position + ring.Tangent * radius, ring.Tangent, new float2(0.5f, 1f));
        Fan(submesh, pole, previous, sides, reverse: false);
    }

    private int CapProfile(PhosMesh mesh, in Ring ring, float radius, int sides, float phi, float direction)
    {
        int v = AddVertices(mesh, sides + 1);
        float c = MathF.Cos(phi), s = MathF.Sin(phi);
        for (int k = 0; k <= sides; k++)
        {
            float a = k / (float)sides * MathF.PI * 2f;
            float3 dir = ring.Normal * MathF.Cos(a) + ring.Side * MathF.Sin(a);
            float3 normal = dir * c + ring.Tangent * (s * direction);
            SetVertex(mesh, v + k, ring.Position + normal * radius, normal, new float2(k / (float)sides, 0f));
        }
        return v;
    }

    // CONFETTI

    // Every chip comes from its own point and a hash of the stroke's seed, so each machine scatters the same
    // chips in the same places and a new point never moves an old chip.
    private const int ChipsPerPoint = 2;

    private void BuildConfetti(PhosMesh mesh, int firstChanged)
    {
        int from = System.Math.Min(firstChanged, _blockVertex.Count);
        if (from < _blockVertex.Count)
        {
            Truncate(mesh, _blockVertex[from], _blockTris[from], 0);
            _blockVertex.RemoveRange(from, _blockVertex.Count - from);
            _blockTris.RemoveRange(from, _blockTris.Count - from);
        }
        var (h, s, v) = _tint.ToHSV();
        for (int i = _blockVertex.Count; i < _count; i++)
        {
            _blockVertex.Add(mesh.VertexCount);
            _blockTris.Add(_main!.Count);
            for (int k = 0; k < ChipsPerPoint; k++)
                EmitChip(mesh, i, k, h, s, v);
        }
    }

    private void EmitChip(PhosMesh mesh, int point, int chip, float hue, float sat, float val)
    {
        uint state = Hash((uint)_seed, (uint)point, (uint)chip);
        float w = MathF.Max(_widths[point], 1e-4f);
        var offset = new float3(Unit(ref state) * 2f - 1f, Unit(ref state) * 2f - 1f, Unit(ref state) * 2f - 1f) * (w * 0.45f);
        var centre = _control[point] + offset;
        float size = w * (0.22f + Unit(ref state) * 0.3f);
        var axis = new float3(Unit(ref state) * 2f - 1f, Unit(ref state) * 2f - 1f, Unit(ref state) * 2f - 1f);
        if (axis.LengthSquared < 1e-6f)
            axis = float3.Up;
        var turn = floatQ.AxisAngleRad(axis.Normalized, Unit(ref state) * MathF.PI * 2f);
        float3 u = turn * float3.Right, n = turn * float3.Up, f = turn * float3.Forward;

        float chipHue = hue + (Unit(ref state) - 0.5f) * 0.16f;
        chipHue -= MathF.Floor(chipHue);
        float chipVal = System.Math.Clamp(val * (0.8f + Unit(ref state) * 0.4f), 0f, 1.5f);
        float chipSat = System.Math.Clamp(sat * (0.85f + Unit(ref state) * 0.3f), 0f, 1f);
        var tint = colorHDR.FromHSV(chipHue, chipSat, chipVal, 1f);
        var vc = new color(tint.r, tint.g, tint.b, 1f);

        int corners = (int)(Unit(ref state) * 3f) switch { 0 => 3, 1 => 4, _ => 6 };
        int v = AddVertices(mesh, corners);
        float spin = Unit(ref state) * MathF.PI * 2f;
        for (int c = 0; c < corners; c++)
        {
            float a = spin + c / (float)corners * MathF.PI * 2f;
            var p = centre + (u * MathF.Cos(a) + f * MathF.Sin(a)) * (size * 0.5f);
            SetVertex(mesh, v + c, p, n, new float2(MathF.Cos(a) * 0.5f + 0.5f, MathF.Sin(a) * 0.5f + 0.5f));
            mesh.RawColors[v + c] = vc;
        }
        int t = _main!.Count;
        _main.IncreaseCount(corners - 2);
        var idx = _main.RawIndices;
        // Corners run from u toward f, which turns clockwise seen from n: n is the front.
        for (int c = 1; c < corners - 1; c++)
        {
            int at = (t + c - 1) * 3;
            idx[at] = v;
            idx[at + 1] = v + c;
            idx[at + 2] = v + c + 1;
        }
    }

    private static uint Hash(uint a, uint b, uint c)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA77u ^ (c + 0x165667B1u) * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        h ^= h >> 12;
        h *= 0x297A2D39u;
        h ^= h >> 15;
        return h == 0 ? 1u : h;
    }

    private static float Unit(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return (state & 0xFFFFFF) / (float)0x1000000;
    }

    // FILL

    // A loop drawn in the air becomes a flat sheet across it. The points are laid onto the plane that fits
    // them best and the outline cut into triangles ear by ear; a loop that crosses itself still fills, just
    // not prettily. Fills are short, so this is redone whole each time.
    private void BuildFill(PhosMesh mesh)
    {
        Truncate(mesh, 0, 0, 0);
        if (_count < 3)
            return;

        float3 centre = float3.Zero;
        for (int i = 0; i < _count; i++)
            centre += _control[i];
        centre /= _count;

        // Newell's normal: the area-weighted normal of the loop, steady even when the hand wobbles.
        float3 normal = float3.Zero;
        for (int i = 0; i < _count; i++)
        {
            var a = _control[i] - centre;
            var b = _control[(i + 1) % _count] - centre;
            normal += float3.Cross(a, b);
        }
        if (normal.LengthSquared < 1e-12f)
            return;
        normal = normal.Normalized;
        float3 axisU = float3.Cross(MathF.Abs(normal.y) < 0.9f ? float3.Up : float3.Right, normal).Normalized;
        float3 axisV = float3.Cross(normal, axisU);

        _flat.Clear();
        _poly.Clear();
        float extent = 1e-6f;
        for (int i = 0; i < _count; i++)
        {
            var d = _control[i] - centre;
            var q = new float2(float3.Dot(d, axisU), float3.Dot(d, axisV));
            _flat.Add(q);
            extent = MathF.Max(extent, MathF.Max(MathF.Abs(q.x), MathF.Abs(q.y)));
        }

        int v = AddVertices(mesh, _count);
        for (int i = 0; i < _count; i++)
        {
            var q = _flat[i];
            var p = centre + axisU * q.x + axisV * q.y;
            SetVertex(mesh, v + i, p, normal, new float2(q.x / (2f * extent) + 0.5f, q.y / (2f * extent) + 0.5f));
            _poly.Add(i);
        }

        // counter-clockwise in the plane, so an ear is a left turn
        float area = 0f;
        for (int i = 0; i < _count; i++)
        {
            var a = _flat[i];
            var b = _flat[(i + 1) % _count];
            area += a.x * b.y - b.x * a.y;
        }
        if (area < 0f)
            _poly.Reverse();

        int guard = _count * _count + 8;
        while (_poly.Count > 3 && guard-- > 0)
        {
            int n = _poly.Count;
            int ear = -1;
            for (int i = 0; i < n; i++)
            {
                int ia = _poly[(i + n - 1) % n], ib = _poly[i], ic = _poly[(i + 1) % n];
                if (IsEar(ia, ib, ic))
                {
                    ear = i;
                    break;
                }
            }
            // Nothing clips cleanly (the loop crosses itself): take the flattest corner and carry on.
            if (ear < 0)
                ear = FlattestCorner();
            int pa = _poly[(ear + n - 1) % n], pb = _poly[ear], pc = _poly[(ear + 1) % n];
            FillTriangle(v + pa, v + pb, v + pc);
            _poly.RemoveAt(ear);
        }
        if (_poly.Count == 3)
            FillTriangle(v + _poly[0], v + _poly[1], v + _poly[2]);
    }

    private bool IsEar(int ia, int ib, int ic)
    {
        var a = _flat[ia];
        var b = _flat[ib];
        var c = _flat[ic];
        float turn = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (turn <= 1e-12f)
            return false;
        foreach (int other in _poly)
        {
            if (other == ia || other == ib || other == ic)
                continue;
            if (Inside(_flat[other], a, b, c))
                return false;
        }
        return true;
    }

    private int FlattestCorner()
    {
        int n = _poly.Count;
        int best = 0;
        float bestTurn = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var a = _flat[_poly[(i + n - 1) % n]];
            var b = _flat[_poly[i]];
            var c = _flat[_poly[(i + 1) % n]];
            float turn = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (turn > bestTurn)
            {
                bestTurn = turn;
                best = i;
            }
        }
        return best;
    }

    private static bool Inside(float2 p, float2 a, float2 b, float2 c)
    {
        float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
        float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
        float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    // The loop was put counter-clockwise about the normal; the engine draws clockwise faces.
    private void FillTriangle(int a, int b, int c)
    {
        int t = _main!.Count;
        _main.IncreaseCount(1);
        var idx = _main.RawIndices;
        idx[t * 3] = a;
        idx[t * 3 + 1] = c;
        idx[t * 3 + 2] = b;
    }

    // SHARED

    private static int AddVertices(PhosMesh mesh, int count)
    {
        int v = mesh.VertexCount;
        mesh.IncreaseVertexCount(count);
        return v;
    }

    private static void SetVertex(PhosMesh mesh, int index, float3 position, float3 normal, float2 uv)
    {
        mesh.RawPositions[index] = position;
        mesh.RawNormals[index] = normal;
        mesh.RawUV0s[index] = uv;
    }

    // Two rings of the same profile, joined the way every tube in the engine is wound.
    private static void Quad(PhosTriangleSubmesh submesh, int lower, int upper, int sides)
    {
        int t = submesh.Count;
        submesh.IncreaseCount(sides * 2);
        var idx = submesh.RawIndices;
        int at = t * 3;
        for (int i = 0; i < sides; i++)
        {
            idx[at++] = lower + i;
            idx[at++] = lower + i + 1;
            idx[at++] = upper + i + 1;
            idx[at++] = lower + i;
            idx[at++] = upper + i + 1;
            idx[at++] = upper + i;
        }
    }

    private static void Fan(PhosTriangleSubmesh submesh, int pole, int ring, int sides, bool reverse)
    {
        int t = submesh.Count;
        submesh.IncreaseCount(sides);
        var idx = submesh.RawIndices;
        int at = t * 3;
        for (int i = 0; i < sides; i++)
        {
            if (reverse)
            {
                idx[at++] = pole;
                idx[at++] = ring + i + 1;
                idx[at++] = ring + i;
            }
            else
            {
                idx[at++] = ring + i;
                idx[at++] = ring + i + 1;
                idx[at++] = pole;
            }
        }
    }
}
