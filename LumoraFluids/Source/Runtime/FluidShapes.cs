// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A shape on a slot as spheres, for kernels that only remove by sphere (a drain). Inside tests and volumes are the
// ledger's (FluidSpawnFill.Inside, ShapeLitres), so a sensor, a spawn volume and a drain all mean the same shape:
// Size is the full extent, a box's sides, an ellipsoid's diameters, a cylinder or disc Size.x by Size.z across and
// Size.y tall, centred on the slot. -xlinka
public static class FluidShapes
{
    // At most `max` world-space spheres that cover the shape. A box or cylinder is tiled along its long axes with
    // spheres sized to reach each tile's corners; a little outside the shape is taken too, which a drain does not mind.
    public static void Spheres(FluidShape shape, float3 size, Slot slot, int max, List<(float3 Center, float Radius)> into)
    {
        into.Clear();
        var scale = slot.GlobalScale;
        var half = new float3(MathF.Max(MathF.Abs(size.x), 1e-4f) * MathF.Abs(scale.x),
                              MathF.Max(MathF.Abs(size.y), 1e-4f) * MathF.Abs(scale.y),
                              MathF.Max(MathF.Abs(size.z), 1e-4f) * MathF.Abs(scale.z)) * 0.5f;
        if (shape is FluidShape.Point or FluidShape.Sphere)
        {
            into.Add((slot.GlobalPosition, MathF.Max(half.x, MathF.Max(half.y, half.z))));
            return;
        }
        float side = MathF.Max(1e-3f, MathF.Min(half.x, MathF.Min(half.y, half.z)));
        int nx = Math.Max(1, (int)MathF.Ceiling(half.x / side));
        int ny = Math.Max(1, (int)MathF.Ceiling(half.y / side));
        int nz = Math.Max(1, (int)MathF.Ceiling(half.z / side));
        while (nx * ny * nz > Math.Max(1, max))
        {
            if (nx >= ny && nx >= nz)
                nx--;
            else if (ny >= nz)
                ny--;
            else
                nz--;
        }
        var cell = new float3(half.x / nx, half.y / ny, half.z / nz);
        float radius = cell.Length;
        var rotation = slot.GlobalRotation;
        var origin = slot.GlobalPosition;
        for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
                for (int z = 0; z < nz; z++)
                {
                    var local = new float3(-half.x + cell.x * (2 * x + 1), -half.y + cell.y * (2 * y + 1), -half.z + cell.z * (2 * z + 1));
                    into.Add((origin + rotation * local, radius));
                }
    }
}
