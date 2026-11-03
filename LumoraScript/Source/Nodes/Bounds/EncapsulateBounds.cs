// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The smallest box holding both. An empty box adds nothing, and an unwired one is Empty Bounds.
[Variadic]
[Node("Math/Bounds/Encapsulate Bounds", Description = "The smallest box holding both.",
    Keywords = "bounding box, aabb, box, union, merge, combine")]
public sealed class EncapsulateBounds : ValueNode
{
    private const double Far = double.PositiveInfinity;

    [return: Port("bounds")]
    public static BoundingBox Run([Default(Far, Far, Far, -Far, -Far, -Far)] BoundingBox a,
        [Default(Far, Far, Far, -Far, -Far, -Far)] BoundingBox b)
    {
        // A box shrunk past itself is empty without being the infinite Empty Bounds, and folding its
        // corners in would pull the union about. -xlinka
        if (b.IsEmpty)
            return a;
        if (a.IsEmpty)
            return b;
        a.Encapsulate(b);
        return a;
    }
}
