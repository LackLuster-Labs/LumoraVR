// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A point on a face counts as inside. An empty box contains nothing.
[Node("Math/Bounds/Contains Point", Description = "Checks whether a point is inside or on the edge of a bounding box.",
    Keywords = "bounding box, aabb, box, inside")]
public sealed class BoundsContainsPoint : ValueNode
{
    [return: Port("contains")]
    public static bool Run(BoundingBox bounds, float3 point) => bounds.Contains(point);
}
