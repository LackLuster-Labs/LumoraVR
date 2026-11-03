// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The box between two opposite corners, which may come in either order.
[Node("Math/Bounds/Bounds From Min Max", Description = "The box between two opposite corners, which may come in either order.",
    Keywords = "bounding box, aabb, box")]
public sealed class BoundsFromMinMax : ValueNode
{
    [return: Port("bounds")]
    public static BoundingBox Run(float3 min, float3 max) => new(
        new float3(MathF.Min(min.x, max.x), MathF.Min(min.y, max.y), MathF.Min(min.z, max.z)),
        new float3(MathF.Max(min.x, max.x), MathF.Max(min.y, max.y), MathF.Max(min.z, max.z)));
}
