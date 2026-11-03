// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Size is the full width, height and depth; a negative one counts as its length.
[Node("Math/Bounds/Bounds From Center Size", Description = "Creates a bounding box from its centre and full width, height and depth.",
    Keywords = "bounding box, aabb, box")]
public sealed class BoundsFromCenterSize : ValueNode
{
    [return: Port("bounds")]
    public static BoundingBox Run(float3 center, float3 size)
    {
        var half = new float3(MathF.Abs(size.x), MathF.Abs(size.y), MathF.Abs(size.z)) * 0.5f;
        return new BoundingBox(center - half, center + half);
    }
}
