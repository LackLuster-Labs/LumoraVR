// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Simulation.SoftBody;

public sealed partial class SoftBodySolver
{
    public SimulationDevice Device = SimulationDevice.Automatic;
    public string ComputeStatus { get; private set; } = "CPU";
    public bool WantsCompute => !_computeFailed && ClothCompute.Create != null && Pressure <= 0f && ShapeRetention <= 0f
        && (Device == SimulationDevice.Gpu || Device == SimulationDevice.Automatic && ParticleCount >= 8192);
    public long GpuBytes => _compute?.AllocatedBytes ?? 0;
    private IClothCompute? _compute;
    private bool _computeFailed;
    private ClothComputeData? _computeData;
    private int[]? _computeEdgeOrder;

    public void ReleaseCompute()
    {
        _compute?.Dispose();
        _compute = null;
        _computeFailed = false;
        _computeData = null;
        _computeEdgeOrder = null;
        ComputeStatus = "CPU";
    }

    private bool TryCompute(float dt, float previousDt, float damping, float ratio,
        float maxMove, bool moving, double time, ISoftBodySpace space)
    {
        var create = ClothCompute.Create;
        string? fallback = ParticleCount == 0 ? "CPU: empty cloth" : Device == SimulationDevice.Cpu ? "CPU selected"
            : Pressure > 0f || ShapeRetention > 0f ? "CPU: pressure or shape retention"
            : Device == SimulationDevice.Automatic && ParticleCount < 8192 ? "CPU: small cloth"
            : create == null ? "CPU: compute unavailable" : null;
        if (fallback != null)
        {
            if (_compute != null) ReleaseCompute();
            ComputeStatus = fallback;
            return false;
        }
        _compute ??= create!();
        _computeData ??= BuildComputeData();
        var data = _computeData;
        var particles = _particles!;
        for (int i = 0; i < particles.Length; i++)
        {
            ref var p = ref data.Points[i];
            p.Position = particles[i].Pos;
            p.Previous = particles[i].Prev;
            p.Pinned = particles[i].Pinned ? 1f : 0f;
            p.Pull = _pull?[i] ?? 0f;
            p.Goal = _goal?[i] ?? space.LocalPointToGlobal(particles[i].RestLocal);
            p.Area = _restArea?[i] ?? 0f;
            p.Pin = _lraPin?[i] ?? -1;
            p.Distance = (_lraRest?[i] ?? 0f) * 1.02f;
        }
        for (int i = 0; i < data.Edges.Length; i++)
        {
            int index = _computeEdgeOrder![i];
            var edge = index < _edges!.Length ? _edges[index] : _bendEdges![index - _edges.Length];
            data.Edges[i] = new ClothEdge { A = edge.A, B = edge.B, Rest = edge.Rest, Ka = edge.Ka, Kb = edge.Kb };
        }
        int iterations = System.Math.Clamp(Iterations, 1, 32);
        float gust = 1f + System.Math.Clamp(WindGustiness, 0f, 2f)
            * (0.6f * MathF.Sin((float)time * 2.3f) + 0.4f * MathF.Sin((float)time * 5.9f + 1.7f));
        var step = new ClothComputeStep
        {
            Dt = dt,
            PreviousDt = previousDt,
            Damping = damping,
            Ratio = ratio,
            MaxMove = maxMove,
            Moving = moving,
            Gravity = Gravity,
            Wind = Wind * gust,
            Iterations = iterations,
            Stretch = CorrectedStiffness(Stiffness, iterations),
            Bend = CorrectedStiffness(BendStiffness, iterations),
        };
        if (!_compute.Step(data, step))
        {
            _computeFailed = true;
            ComputeStatus = _compute.Status;
            return false;
        }
        for (int i = 0; i < particles.Length; i++)
        {
            particles[i].Pos = data.Points[i].Position;
            particles[i].Prev = data.Points[i].Previous;
        }
        ComputeStatus = "GPU constraints; CPU contacts";
        return true;
    }

    private ClothComputeData BuildComputeData()
    {
        var data = new ClothComputeData { Points = new ClothPoint[ParticleCount], Triangles = _restIndices! };
        var adjacent = new List<int>[ParticleCount];
        for (int i = 0; i < adjacent.Length; i++) adjacent[i] = new List<int>();
        for (int t = 0; t < data.Triangles.Length / 3; t++)
            for (int k = 0; k < 3; k++) adjacent[data.Triangles[t * 3 + k]].Add(t);
        var flat = new List<int>();
        for (int i = 0; i < adjacent.Length; i++)
        {
            data.Points[i].FirstTriangle = flat.Count;
            data.Points[i].TriangleCount = adjacent[i].Count;
            flat.AddRange(adjacent[i]);
        }
        data.Adjacent = flat.ToArray();
        var order = new List<int>();
        var starts = new List<int> { 0 };
        void Color(Edge[] edges, int offset)
        {
            var groups = new List<List<int>>();
            var used = new List<HashSet<int>>();
            for (int i = 0; i < edges.Length; i++)
            {
                var edge = edges[i];
                int group = 0;
                while (group < used.Count && (used[group].Contains(edge.A) || used[group].Contains(edge.B))) group++;
                if (group == used.Count) { used.Add(new HashSet<int>()); groups.Add(new List<int>()); }
                used[group].Add(edge.A);
                used[group].Add(edge.B);
                groups[group].Add(i + offset);
            }
            foreach (var group in groups) { order.AddRange(group); starts.Add(order.Count); }
        }
        Color(_edges!, 0);
        data.StretchGroups = starts.Count - 1;
        if (_bendEdges != null) Color(_bendEdges, _edges!.Length);
        data.GroupStarts = starts.ToArray();
        data.Edges = new ClothEdge[order.Count];
        _computeEdgeOrder = order.ToArray();
        return data;
    }
}
