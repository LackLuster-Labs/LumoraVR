// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Simulation.SoftBody;

namespace Lumora.Fluids;

// Read the body's public deformed surface and welded particle map, without reaching into its private solver. -xlinka
public sealed class FluidSoftBodyGather
{
    private readonly FluidSpace _space;
    private readonly List<SquishyBody> _bodies = new();
    private readonly Dictionary<SquishyBody, (SoftBodyMeshMap Map, int[] Vertices, float3[] Last, bool Seen)> _history = new();
    private readonly List<(float3 Min, float3 Max)> _bounds = new();
    private double _nextScan;

    public FluidSoftBodyGather(FluidSpace space) => _space = space;

    public int Spheres { get; private set; }

    public void Gather(FluidFrameBlock block)
    {
        Spheres = 0;
        double now = _space.World.Time.TotalTime;
        if (now >= _nextScan)
        {
            _nextScan = now + 1.0;
            _bodies.Clear();
            _space.World.RootSlot.GetComponentsInChildren(_bodies);
            var gone = new List<SquishyBody>();
            foreach (var body in _history.Keys)
                if (body.IsDestroyed || !_bodies.Contains(body))
                    gone.Add(body);
            foreach (var body in gone)
                _history.Remove(body);
        }
        GatherBounds();
        if (_bounds.Count == 0)
            return;
        foreach (var body in _bodies)
        {
            var deform = body.Deformed;
            var map = body.ParticleMap;
            var mesh = deform?.PhosMesh;
            if (body.IsDestroyed || !body.Enabled.Value || !body.IsBuilt || !body.Slot.IsActive || deform == null
                || deform.IsDestroyed || map == null || mesh == null || map.VertexCount != mesh.VertexCount)
                continue;
            if (!_history.TryGetValue(body, out var history) || !ReferenceEquals(history.Map, map))
            {
                var vertices = new int[map.ParticleCount];
                Array.Fill(vertices, -1);
                for (int v = 0; v < map.VertexCount; v++)
                {
                    int p = map.ParticleOf(v);
                    if (vertices[p] < 0)
                        vertices[p] = v;
                }
                history = (map, vertices, new float3[vertices.Length], false);
            }
            var scale = deform.Slot.GlobalScale;
            float factor = MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
            float radius = MathF.Max(body.ParticleRadius.Value, map.CellSize * 0.5f * factor);
            if (!(radius > 0f) || !float.IsFinite(radius))
                continue;
            bool moved = false;
            float3 lo = new(float.MaxValue), hi = new(float.MinValue);
            for (int i = 0; i < history.Vertices.Length; i++)
            {
                int vertex = history.Vertices[i];
                if (vertex < 0)
                    continue;
                var point = deform.Slot.LocalPointToGlobal(mesh.RawPositions[vertex]);
                if (!Finite(point))
                {
                    history.Last[i] = new float3(float.NaN);
                    continue;
                }
                var last = history.Seen && Finite(history.Last[i]) ? history.Last[i] : point;
                history.Last[i] = point;
                if (!Near(point, radius) && !Near(last, radius))
                    continue;
                block.SoftSpheres.Add(new FluidSoftSphere { Now = new float4(point, radius), Last = new float4(last, radius) });
                Spheres++;
                if (float3.DistanceSquared(point, last) > 1e-6f)
                {
                    moved = true;
                    lo = Min(lo, Min(point, last) - new float3(radius));
                    hi = Max(hi, Max(point, last) + new float3(radius));
                }
            }
            history.Seen = true;
            _history[body] = history;
            if (moved)
            {
                for (int s = 0; s < _space.Systems.Count; s++)
                {
                    int frame = _space.Packer.FrameOf(s);
                    var center = _space.Packer.PointToFrame(frame, (lo + hi) * 0.5f);
                    var half = (hi - lo) * 0.5f;
                    if (frame != 0)
                    {
                        var slot = _space.SystemAt(s)?.Slot;
                        float r = half.Length;
                        if (slot != null)
                            r = MathF.Max(slot.GlobalVectorToLocal(new float3(r, 0f, 0f)).Length,
                                MathF.Max(slot.GlobalVectorToLocal(new float3(0f, r, 0f)).Length, slot.GlobalVectorToLocal(new float3(0f, 0f, r)).Length));
                        half = new float3(r);
                    }
                    block.Commands.Add(new FluidGpuCommand { Op = (uint)FluidGpuOp.WakeBox, System = (uint)s,
                        A = new float4(center, 0f), B = new float4(half, 0f) });
                }
            }
        }
    }

    private void GatherBounds()
    {
        _bounds.Clear();
        for (int i = 0; i < _space.Systems.Count; i++)
        {
            var system = _space.SystemAt(i);
            if (system == null || system.IsDestroyed || !system.Enabled.Value)
                continue;
            ref readonly var status = ref _space.StatusOf(i);
            var lo = status.BoundsMin;
            var hi = status.BoundsMax;
            if (status.Live == 0 || !Finite(lo) || !Finite(hi))
            {
                var half = system.Bounds.Value * 0.5f;
                if (half.x <= 0f || half.y <= 0f || half.z <= 0f)
                    half = new float3(1f);
                var scale = system.Slot.GlobalScale;
                float r = half.Length * MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
                var center = system.Slot.LocalPointToGlobal(system.BoundsCenter.Value);
                _bounds.Add((center - new float3(r + 0.1f), center + new float3(r + 0.1f)));
                continue;
            }
            if (_space.Packer.FrameOf(i) != 0)
            {
                float r = (hi - lo).Length * 0.5f;
                var center = system.Slot.LocalPointToGlobal((hi + lo) * 0.5f);
                var scale = system.Slot.GlobalScale;
                r *= MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
                lo = center - new float3(r);
                hi = center + new float3(r);
            }
            _bounds.Add((lo - new float3(0.1f), hi + new float3(0.1f)));
        }
    }

    private bool Near(float3 p, float radius)
    {
        foreach (var (lo, hi) in _bounds)
            if (p.x + radius >= lo.x && p.x - radius <= hi.x && p.y + radius >= lo.y && p.y - radius <= hi.y
                && p.z + radius >= lo.z && p.z - radius <= hi.z)
                return true;
        return false;
    }

    private static bool Finite(float3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static float3 Min(float3 a, float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));
    private static float3 Max(float3 a, float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
}
