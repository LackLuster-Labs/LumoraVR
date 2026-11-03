// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Grown just enough to hold the point. An empty box becomes the point itself, and unwired bounds are Empty
// Bounds.
[Node("Math/Bounds/Grow To Point", Description = "Grown just enough to hold the point.",
    Keywords = "bounding box, aabb, box, include point, add point")]
public sealed class GrowBoundsToPoint : ValueNode
{
    private const double Far = double.PositiveInfinity;

    [return: Port("grown")]
    public static BoundingBox Run([Default(Far, Far, Far, -Far, -Far, -Far)] BoundingBox bounds, float3 point)
    {
        if (bounds.IsEmpty)
            return new BoundingBox(point, point);
        bounds.Encapsulate(point);
        return bounds;
    }
}
