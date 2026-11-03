// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Whether the boxes share any point; boxes that only touch at a face count. An empty box meets nothing.
[Node("Math/Bounds/Intersects", Description = "Whether the boxes share any point; boxes that only touch at a face count.",
    Keywords = "bounding box, aabb, box, overlap, collide")]
public sealed class BoundsIntersect : ValueNode
{
    [return: Port("intersect")]
    public static bool Run(BoundingBox a, BoundingBox b) => !a.IsEmpty && !b.IsEmpty && a.Intersects(b);
}
