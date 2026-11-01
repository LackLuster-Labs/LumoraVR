// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Fluids;

// One mesh's collision grid as the GPU reads it, in collider space: the triangles, a cell table and the per-cell
// triangle lists. The header's start fields are zero here; the packer offsets them when it lays several grids into
// one frame block. Immutable once built, so worlds share it freely. -xlinka
public sealed class FluidTriangleGrid
{
    private FluidTriangleGrid(FluidTriangleGridHeader header, FluidTriangle[] triangles, FluidGridCell[] cells, uint[] indices, int vertexCount, int sourceTriangles)
    {
        Header = header;
        Triangles = triangles;
        Cells = cells;
        Indices = indices;
        VertexCount = vertexCount;
        SourceTriangles = sourceTriangles;
    }

    public FluidTriangleGridHeader Header { get; }

    public FluidTriangle[] Triangles { get; }

    public FluidGridCell[] Cells { get; }

    public uint[] Indices { get; }

    public int TriangleCount => Triangles.Length;

    public int VertexCount { get; }

    public int SourceTriangles { get; }

    public long Bytes => (long)Triangles.Length * FluidTriangle.Size + (long)Cells.Length * FluidGridCell.Size + Indices.Length * 4L;

    public bool IsStale(PhosMesh mesh) => mesh.VertexCount != VertexCount || CountTriangles(mesh) != SourceTriangles;

    public static FluidTriangleGrid Build(PhosMesh mesh)
    {
        var positions = mesh.RawPositions;
        int vertexCount = Math.Min(mesh.VertexCount, positions?.Length ?? 0);
        int source = CountTriangles(mesh);
        var triangles = new List<FluidTriangle>(source);
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh is not PhosTriangleSubmesh)
                continue;
            var raw = submesh.RawIndices;
            int count = Math.Min(submesh.IndexCount, raw?.Length ?? 0) / 3 * 3;
            for (int i = 0; i < count; i += 3)
            {
                int a = raw![i], b = raw[i + 1], c = raw[i + 2];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                    continue;
                var t = new FluidTriangle { A = positions![a], B = positions[b], C = positions[c] };
                // Degenerate and non-finite triangles have no side to collide with.
                if (!Finite(t.A) || !Finite(t.B) || !Finite(t.C) || float3.Cross(t.B - t.A, t.C - t.A).LengthSquared < 1e-14f)
                    continue;
                triangles.Add(t);
            }
        }
        if (triangles.Count == 0)
            return new FluidTriangleGrid(default, Array.Empty<FluidTriangle>(), Array.Empty<FluidGridCell>(), Array.Empty<uint>(), vertexCount, source);

        var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new float3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var t in triangles)
        {
            Grow(ref min, ref max, t.A);
            Grow(ref min, ref max, t.B);
            Grow(ref min, ref max, t.C);
        }
        var size = max - min;
        float volume = MathF.Max(size.x, 1e-3f) * MathF.Max(size.y, 1e-3f) * MathF.Max(size.z, 1e-3f);
        float cell = MathF.Cbrt(volume * FluidTriangleGridCache.TrianglesPerCell / triangles.Count);
        float longest = MathF.Max(size.x, MathF.Max(size.y, size.z));
        cell = MathF.Max(cell, longest / FluidTriangleGridCache.MaxCellsPerAxis);
        cell = MathF.Max(cell, 1e-3f);
        uint dx = Dim(size.x, cell), dy = Dim(size.y, cell), dz = Dim(size.z, cell);

        // Two passes: count each cell's triangles, then lay the lists out back to back in cell order.
        var counts = new uint[dx * dy * dz];
        foreach (var t in triangles)
            Visit(t, min, cell, dx, dy, dz, index => counts[index]++);
        var cells = new FluidGridCell[counts.Length];
        uint total = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i].Start = total;
            cells[i].Count = 0;
            total += counts[i];
        }
        var indices = new uint[total];
        for (int ti = 0; ti < triangles.Count; ti++)
        {
            uint triangle = (uint)ti;
            Visit(triangles[ti], min, cell, dx, dy, dz, index =>
            {
                ref var c = ref cells[index];
                indices[c.Start + c.Count] = triangle;
                c.Count++;
            });
        }

        var header = new FluidTriangleGridHeader
        {
            Origin = min,
            CellSize = cell,
            DimX = dx,
            DimY = dy,
            DimZ = dz,
            TriangleCount = (uint)triangles.Count,
        };
        return new FluidTriangleGrid(header, triangles.ToArray(), cells, indices, vertexCount, source);
    }

    private static int CountTriangles(PhosMesh mesh)
    {
        int count = 0;
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh is PhosTriangleSubmesh)
                count += submesh.Count;
        }
        return count;
    }

    private static uint Dim(float extent, float cell) => (uint)Math.Clamp((int)MathF.Ceiling(MathF.Max(extent, 1e-4f) / cell), 1, FluidTriangleGridCache.MaxCellsPerAxis);

    private static void Visit(in FluidTriangle t, float3 min, float cell, uint dx, uint dy, uint dz, Action<int> visit)
    {
        var lo = new float3(MathF.Min(t.A.x, MathF.Min(t.B.x, t.C.x)), MathF.Min(t.A.y, MathF.Min(t.B.y, t.C.y)), MathF.Min(t.A.z, MathF.Min(t.B.z, t.C.z)));
        var hi = new float3(MathF.Max(t.A.x, MathF.Max(t.B.x, t.C.x)), MathF.Max(t.A.y, MathF.Max(t.B.y, t.C.y)), MathF.Max(t.A.z, MathF.Max(t.B.z, t.C.z)));
        int x0 = Cell(lo.x - min.x, cell, dx), x1 = Cell(hi.x - min.x, cell, dx);
        int y0 = Cell(lo.y - min.y, cell, dy), y1 = Cell(hi.y - min.y, cell, dy);
        int z0 = Cell(lo.z - min.z, cell, dz), z1 = Cell(hi.z - min.z, cell, dz);
        for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    visit((int)((z * dy + y) * dx + x));
    }

    private static int Cell(float offset, float cell, uint dim) => Math.Clamp((int)MathF.Floor(offset / cell), 0, (int)dim - 1);

    private static void Grow(ref float3 min, ref float3 max, float3 p)
    {
        min = new float3(MathF.Min(min.x, p.x), MathF.Min(min.y, p.y), MathF.Min(min.z, p.z));
        max = new float3(MathF.Max(max.x, p.x), MathF.Max(max.y, p.y), MathF.Max(max.z, p.z));
    }

    private static bool Finite(float3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
}
