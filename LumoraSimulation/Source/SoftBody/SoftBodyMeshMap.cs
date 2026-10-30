// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Simulation.SoftBody;

// Which particle each vertex of a drawn mesh rides on.
//
// A drawn mesh splits a vertex wherever its UVs or its hard edges do, so one point of the surface can be three
// vertices sitting on the same spot. Simulated one particle each, those copies share no edge and the body opens
// along every seam the moment it moves. So vertices on the same spot become one particle first.
//
// Past the particle cap the welded points are gathered into grid cells, one particle per occupied cell, with the
// cell size searched for so the count lands under the cap. Each vertex then rides the nearest few cell particles,
// moved by how far they have moved, so a dense import bends smoothly on a coarse body instead of breaking into
// blocks. Under the cap every vertex rides exactly its own particle and nothing is interpolated. -xlinka
public sealed class SoftBodyMeshMap
{
    private const int MaxInfluences = 4;

    public int VertexCount { get; private set; }

    // Distinct spots in the mesh before any simplifying.
    public int WeldedCount { get; private set; }

    public int ParticleCount { get; private set; }

    public bool Simplified { get; private set; }

    // Cell edge used when simplified, in the mesh's own units; 0 when not.
    public float CellSize { get; private set; }

    // Rest position of every particle: the mean of the spots it gathers.
    public float3[] ParticleRest { get; private set; } = Array.Empty<float3>();

    // Particle triangles with the collapsed and repeated ones left out.
    public int[] ParticleTriangles { get; private set; } = Array.Empty<int>();

    private int[] _vertexParticle = Array.Empty<int>();   // the particle each vertex belongs to
    private int[] _groupStart = Array.Empty<int>();       // per particle, into _groupVertex
    private int[] _groupVertex = Array.Empty<int>();       // one vertex standing for each welded spot
    private int[] _influence = Array.Empty<int>();        // vertex * MaxInfluences
    private float[] _weight = Array.Empty<float>();

    public int ParticleOf(int vertex) => _vertexParticle[vertex];

    public static SoftBodyMeshMap Build(float3[] positions, int[] triangles, int maxParticles)
    {
        var map = new SoftBodyMeshMap();
        map.Create(positions, triangles, maxParticles);
        return map;
    }

    private void Create(float3[] positions, int[] triangles, int maxParticles)
    {
        int n = positions.Length;
        VertexCount = n;

        // WELD. Spots within a hair of each other, a millionth of the mesh's size. Keyed on the exact bits it left
        // every generated round shape open: a sphere or a rounded box closes its seam with a column at a full turn,
        // and sin(2 pi) in single precision is 1.7e-7, not 0, so the closing column sat a ten-millionth of a metre off
        // the first and never joined it. The body was slit from pole to pole, and the bottom pole (sin(pi) is not 0
        // either) was a ring of separate particles on one spot. Open, it counted as a sheet in soft contacts, and a
        // heap of them shivered at the slits for good and never slept. -xlinka
        var spotOf = new int[n];
        var spots = new List<int>();
        float tolerance = WeldTolerance(positions);
        float inverse = 1f / tolerance;
        float toleranceSq = tolerance * tolerance;
        var cells = new Dictionary<(int, int, int), int>(n);
        var nextInCell = new List<int>(n);
        for (int v = 0; v < n; v++)
        {
            var p = positions[v];
            int spot = -1;
            if (float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z))
            {
                int cx = (int)MathF.Floor(p.x * inverse), cy = (int)MathF.Floor(p.y * inverse), cz = (int)MathF.Floor(p.z * inverse);
                for (int dz = -1; dz <= 1 && spot < 0; dz++)
                    for (int dy = -1; dy <= 1 && spot < 0; dy++)
                        for (int dx = -1; dx <= 1 && spot < 0; dx++)
                        {
                            if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out int s))
                                continue;
                            for (; s >= 0; s = nextInCell[s])
                            {
                                if (float3.DistanceSquared(positions[spots[s]], p) <= toleranceSq)
                                {
                                    spot = s;
                                    break;
                                }
                            }
                        }
                if (spot < 0)
                {
                    spot = spots.Count;
                    spots.Add(v);
                    var key = (cx, cy, cz);
                    nextInCell.Add(cells.TryGetValue(key, out int head) ? head : -1);
                    cells[key] = spot;
                }
            }
            else
            {
                spot = spots.Count;
                spots.Add(v);
                nextInCell.Add(-1);
            }
            spotOf[v] = spot;
        }
        int spotCount = spots.Count;
        WeldedCount = spotCount;

        var spotPos = new float3[spotCount];
        for (int s = 0; s < spotCount; s++)
            spotPos[s] = positions[spots[s]];

        // GATHER. Identity under the cap, grid cells over it.
        int[] particleOfSpot;
        int particleCount;
        if (maxParticles > 0 && spotCount > maxParticles)
        {
            float cell = FindCellSize(spotPos, maxParticles);
            particleOfSpot = AssignCells(spotPos, cell, out particleCount);
            Simplified = true;
            CellSize = cell;
        }
        else
        {
            particleOfSpot = new int[spotCount];
            for (int s = 0; s < spotCount; s++)
                particleOfSpot[s] = s;
            particleCount = spotCount;
        }
        ParticleCount = particleCount;

        _vertexParticle = new int[n];
        for (int v = 0; v < n; v++)
            _vertexParticle[v] = particleOfSpot[spotOf[v]];

        // Members per particle, as the vertex standing for each of its spots.
        var counts = new int[particleCount + 1];
        for (int s = 0; s < spotCount; s++)
            counts[particleOfSpot[s] + 1]++;
        for (int p = 0; p < particleCount; p++)
            counts[p + 1] += counts[p];
        _groupStart = counts;
        _groupVertex = new int[spotCount];
        var fill = new int[particleCount];
        for (int s = 0; s < spotCount; s++)
        {
            int p = particleOfSpot[s];
            _groupVertex[_groupStart[p] + fill[p]++] = spots[s];
        }

        var rest = new float3[particleCount];
        Average(positions, rest);
        ParticleRest = rest;

        // Particle triangles, each kept once whatever its winding was.
        var tris = new List<int>(triangles.Length);
        var seen = new HashSet<(int, int, int)>();
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int a = _vertexParticle[triangles[t]];
            int b = _vertexParticle[triangles[t + 1]];
            int c = _vertexParticle[triangles[t + 2]];
            if (a == b || b == c || a == c)
                continue;
            if (!seen.Add(Sorted(a, b, c)))
                continue;
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }
        ParticleTriangles = tris.ToArray();

        BuildInfluences(positions, particleCount);
    }

    // The members' mean per particle, from a value per vertex.
    public void Average(float3[] perVertex, float3[] perParticle)
    {
        for (int p = 0; p < ParticleCount; p++)
        {
            int start = _groupStart[p], end = _groupStart[p + 1];
            var sum = float3.Zero;
            for (int k = start; k < end; k++)
                sum += perVertex[_groupVertex[k]];
            perParticle[p] = end > start ? sum / (end - start) : float3.Zero;
        }
    }

    public void Average(float[] perVertex, float[] perParticle)
    {
        for (int p = 0; p < ParticleCount; p++)
        {
            int start = _groupStart[p], end = _groupStart[p + 1];
            float sum = 0f;
            for (int k = start; k < end; k++)
                sum += perVertex[_groupVertex[k]];
            perParticle[p] = end > start ? sum / (end - start) : 0f;
        }
    }

    // Every vertex at its reference spot moved by its particles' move from theirs.
    public void Displace(float3[] particleNow, float3[] particleReference, float3[] vertexReference, float3[] output)
    {
        int n = VertexCount;
        if (!Simplified)
        {
            for (int v = 0; v < n; v++)
            {
                int p = _vertexParticle[v];
                output[v] = vertexReference[v] + (particleNow[p] - particleReference[p]);
            }
            return;
        }

        var influence = _influence;
        var weight = _weight;
        for (int v = 0; v < n; v++)
        {
            int at = v * MaxInfluences;
            var move = float3.Zero;
            for (int k = 0; k < MaxInfluences; k++)
            {
                float w = weight[at + k];
                if (w <= 0f)
                    break;
                int p = influence[at + k];
                move += (particleNow[p] - particleReference[p]) * w;
            }
            output[v] = vertexReference[v] + move;
        }
    }

    // Under the cap a vertex rides its own particle and nothing else. Over it, the nearest few of its own cell and the
    // cells it shares an edge with, by inverse square distance at rest, softened by a share of the cell so a vertex
    // sitting right on one centre still feels its neighbours a little.
    private void BuildInfluences(float3[] positions, int particleCount)
    {
        if (!Simplified)
            return;

        var neighbours = new List<int>[particleCount];
        for (int p = 0; p < particleCount; p++)
            neighbours[p] = new List<int>(8);
        var tris = ParticleTriangles;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            Link(neighbours, tris[t], tris[t + 1]);
            Link(neighbours, tris[t + 1], tris[t + 2]);
            Link(neighbours, tris[t + 2], tris[t]);
        }

        int n = VertexCount;
        _influence = new int[n * MaxInfluences];
        _weight = new float[n * MaxInfluences];
        float soft = CellSize * 0.35f;
        float softSq = MathF.Max(soft * soft, 1e-12f);
        var best = new int[MaxInfluences];
        var bestDist = new float[MaxInfluences];
        for (int v = 0; v < n; v++)
        {
            int own = _vertexParticle[v];
            var pos = positions[v];
            int found = 0;
            Consider(own);
            foreach (int other in neighbours[own])
                Consider(other);

            float total = 0f;
            for (int k = 0; k < found; k++)
                total += 1f / (bestDist[k] + softSq);
            int at = v * MaxInfluences;
            for (int k = 0; k < found; k++)
            {
                _influence[at + k] = best[k];
                _weight[at + k] = 1f / (bestDist[k] + softSq) / total;
            }

            void Consider(int particle)
            {
                float d = float3.DistanceSquared(pos, ParticleRest[particle]);
                if (found < MaxInfluences)
                {
                    best[found] = particle;
                    bestDist[found] = d;
                    found++;
                    return;
                }
                int worst = 0;
                for (int k = 1; k < MaxInfluences; k++)
                    if (bestDist[k] > bestDist[worst])
                        worst = k;
                if (d < bestDist[worst])
                {
                    best[worst] = particle;
                    bestDist[worst] = d;
                }
            }
        }
    }

    private static void Link(List<int>[] neighbours, int a, int b)
    {
        if (!neighbours[a].Contains(b))
            neighbours[a].Add(b);
        if (!neighbours[b].Contains(a))
            neighbours[b].Add(a);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    // Smallest cell that keeps the occupied count under the cap, searched between one cell over the whole mesh and a
    // cell far finer than any edge. Geometric steps, since the count goes with the inverse square or cube of the size.
    // A millionth of the mesh's largest extent, never below a ten-millionth of a unit.
    private static float WeldTolerance(float3[] positions)
    {
        var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new float3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z))
                continue;
            min = new float3(MathF.Min(min.x, p.x), MathF.Min(min.y, p.y), MathF.Min(min.z, p.z));
            max = new float3(MathF.Max(max.x, p.x), MathF.Max(max.y, p.y), MathF.Max(max.z, p.z));
        }
        float extent = max.x >= min.x ? MathF.Max(max.x - min.x, MathF.Max(max.y - min.y, max.z - min.z)) : 0f;
        return MathF.Max(extent * 1e-6f, 1e-7f);
    }

    private static float FindCellSize(float3[] spots, int maxParticles)
    {
        var min = spots[0];
        var max = spots[0];
        for (int i = 1; i < spots.Length; i++)
        {
            var p = spots[i];
            min = new float3(MathF.Min(min.x, p.x), MathF.Min(min.y, p.y), MathF.Min(min.z, p.z));
            max = new float3(MathF.Max(max.x, p.x), MathF.Max(max.y, p.y), MathF.Max(max.z, p.z));
        }
        var size = max - min;
        float hi = MathF.Max(MathF.Max(size.x, size.y), MathF.Max(size.z, 1e-6f)) * 1.001f;
        float lo = hi / 4096f;
        var cells = new HashSet<(int, int, int)>();
        for (int iteration = 0; iteration < 18; iteration++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (CountCells(spots, min, mid, cells, maxParticles) <= maxParticles)
                hi = mid;
            else
                lo = mid;
            if (hi / lo < 1.02f)
                break;
        }
        return hi;
    }

    private static int CountCells(float3[] spots, float3 origin, float cell, HashSet<(int, int, int)> cells, int stopAbove)
    {
        cells.Clear();
        float inv = 1f / cell;
        for (int i = 0; i < spots.Length; i++)
        {
            cells.Add(CellOf(spots[i], origin, inv));
            if (cells.Count > stopAbove)
                return cells.Count;
        }
        return cells.Count;
    }

    private static int[] AssignCells(float3[] spots, float cell, out int count)
    {
        var min = spots[0];
        for (int i = 1; i < spots.Length; i++)
            min = new float3(MathF.Min(min.x, spots[i].x), MathF.Min(min.y, spots[i].y), MathF.Min(min.z, spots[i].z));
        float inv = 1f / cell;
        var index = new Dictionary<(int, int, int), int>();
        var result = new int[spots.Length];
        for (int i = 0; i < spots.Length; i++)
        {
            var key = CellOf(spots[i], min, inv);
            if (!index.TryGetValue(key, out int p))
            {
                p = index.Count;
                index[key] = p;
            }
            result[i] = p;
        }
        count = index.Count;
        return result;
    }

    private static (int, int, int) CellOf(in float3 p, in float3 origin, float inv)
        => ((int)MathF.Floor((p.x - origin.x) * inv), (int)MathF.Floor((p.y - origin.y) * inv), (int)MathF.Floor((p.z - origin.z) * inv));
}
