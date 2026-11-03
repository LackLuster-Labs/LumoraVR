// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Size is the full width, height and depth; extents are half of it. An empty box has its min at infinity
// and its max at minus infinity, and its centre is not a number.
[Node("Math/Bounds/Bounds Parts", Description = "Splits a bounding box into its corners, centre, size and half-size extents.",
    Keywords = "bounding box, aabb, box, center, size, extents, min, max")]
public sealed class BoundsParts : ValueNode
{
    public static void Run(BoundingBox bounds, out float3 center, out float3 size, out float3 extents, out float3 min, out float3 max)
    {
        center = bounds.Center;
        size = bounds.Size;
        extents = bounds.Extents;
        min = bounds.Min;
        max = bounds.Max;
    }
}
