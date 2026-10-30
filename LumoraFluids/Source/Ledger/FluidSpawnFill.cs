// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Voxelises a spawn volume into ledger cells on the host's CPU. Pure geometry, so a headless host spawns puddles and
// every GPU machine reseeds its particles from the cells. Each cell is sampled on a 6 x 6 x 6 grid, so an edge cell
// gets the fraction of it the shape covers, and the whole is scaled down to Litres when the volume sets one. It runs
// once: the volume's saved Spawned flag goes up with it, so a saved world never doubles a puddle and a Clear stays
// clear. -xlinka
public static class FluidSpawnFill
{
    public const int Samples = 6;

    // Whether a point in the shape's own space is inside it. Size is the shape's full extent: a disc or cylinder is
    // Size.x by Size.z across and Size.y tall, a sphere is an ellipsoid of those diameters, all centred on the slot.
    public static bool Inside(FluidShape shape, float3 size, float3 p)
    {
        float hx = Math.Max(size.x * 0.5f, 1e-6f);
        float hy = Math.Max(size.y * 0.5f, 1e-6f);
        float hz = Math.Max(size.z * 0.5f, 1e-6f);
        switch (shape)
        {
            case FluidShape.Box:
                return Math.Abs(p.x) <= hx && Math.Abs(p.y) <= hy && Math.Abs(p.z) <= hz;
            case FluidShape.Disc:
            case FluidShape.Cylinder:
            {
                float u = p.x / hx;
                float w = p.z / hz;
                return u * u + w * w <= 1f && Math.Abs(p.y) <= hy;
            }
            case FluidShape.Sphere:
            {
                float u = p.x / hx;
                float v = p.y / hy;
                float w = p.z / hz;
                return u * u + v * v + w * w <= 1f;
            }
            default:
                return false;
        }
    }

    // The shape's volume in litres, analytically.
    public static double ShapeLitres(FluidShape shape, float3 size)
    {
        double v = shape switch
        {
            FluidShape.Box => (double)size.x * size.y * size.z,
            FluidShape.Disc or FluidShape.Cylinder => Math.PI * 0.25 * size.x * size.z * size.y,
            FluidShape.Sphere => Math.PI / 6.0 * size.x * size.y * size.z,
            _ => 0,
        };
        return v * 1000.0;
    }

    // The cells a volume fills, in the system's space. Returns the litres they hold.
    public static double Cells(FluidSpawnVolume volume, FluidSystem system, float cellSize, int substance, int origin, List<int4> output)
    {
        output.Clear();
        var shape = volume.Shape.Value;
        var size = volume.Size.Value;
        if (shape == FluidShape.Point || volume.Slot == null || system.Slot == null || cellSize <= 0f)
            return 0;

        var from = volume.Slot;
        var to = system.Slot;
        float3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        float3 max = new(float.MinValue, float.MinValue, float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var local = new float3((corner & 1) != 0 ? size.x * 0.5f : -size.x * 0.5f,
                (corner & 2) != 0 ? size.y * 0.5f : -size.y * 0.5f,
                (corner & 4) != 0 ? size.z * 0.5f : -size.z * 0.5f);
            var p = to.GlobalPointToLocal(from.LocalPointToGlobal(local));
            min = new float3(Math.Min(min.x, p.x), Math.Min(min.y, p.y), Math.Min(min.z, p.z));
            max = new float3(Math.Max(max.x, p.x), Math.Max(max.y, p.y), Math.Max(max.z, p.z));
        }
        FluidCell.CellOf(min, cellSize, out int x0, out int y0, out int z0);
        FluidCell.CellOf(max, cellSize, out int x1, out int y1, out int z1);

        double cellLitres = FluidCell.CellLitres(cellSize);
        var fractions = new List<(int X, int Y, int Z, float Fraction)>();
        double total = 0;
        float step = cellSize / Samples;
        for (int ix = x0; ix <= x1; ix++)
        for (int iy = y0; iy <= y1; iy++)
        for (int iz = z0; iz <= z1; iz++)
        {
            int inside = 0;
            for (int a = 0; a < Samples; a++)
            for (int b = 0; b < Samples; b++)
            for (int c = 0; c < Samples; c++)
            {
                var p = new float3(ix * cellSize + (a + 0.5f) * step, iy * cellSize + (b + 0.5f) * step, iz * cellSize + (c + 0.5f) * step);
                if (Inside(shape, size, from.GlobalPointToLocal(to.LocalPointToGlobal(p))))
                    inside++;
            }
            if (inside == 0)
                continue;
            float fraction = inside / (float)(Samples * Samples * Samples);
            fractions.Add((ix, iy, iz, fraction));
            total += fraction * cellLitres;
        }

        double want = volume.Litres.Value;
        double scale = want > 0 && total > want ? want / total : 1.0;
        int flags = volume.StartSettled.Value ? FluidCell.FlagOnSurface : FluidCell.FlagMoving;
        double written = 0;
        foreach (var (x, y, z, fraction) in fractions)
        {
            int fill = (int)Math.Round(fraction * scale * FluidCell.FillMax);
            if (fill <= 0)
                continue;
            var cell = FluidCell.Pack(x, y, z, fill, origin, substance, flags, FluidCell.NoAnchor, 0);
            output.Add(cell);
            written += FluidCell.Litres(cell, cellSize);
        }
        return written;
    }

    // Host only: proposes the volume's cells to the ledger and sets Spawned, once. Returns the litres proposed.
    public static double Fill(FluidSpawnVolume volume, FluidLedger ledger)
    {
        if (volume == null || ledger == null || volume.Spawned.Value || volume.World is not { IsAuthority: true } world)
            return 0;
        var system = ledger.System;
        int substance = 0;
        if (volume.Substance.Target is { } wanted)
        {
            int at = system.Substances.IndexOf(wanted);
            substance = Math.Max(0, at);
        }
        var cells = new List<int4>();
        double litres = Cells(volume, system, ledger.CellSize, substance, 0, cells);
        foreach (var cell in cells)
            ledger.Propose(cell);
        using (world.DataModelPermissions?.EnterSystemBypass())
            volume.Spawned.Value = true;
        return litres;
    }
}
