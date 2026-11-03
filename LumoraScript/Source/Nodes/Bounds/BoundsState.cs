// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Empty: min is past max on some axis, so the box holds no point, as Empty Bounds gives. Valid: not empty
// and every corner a finite number, so the box can be measured and moved. A box holding a number that is not
// finite is neither.
[Node("Math/Bounds/Bounds State", Description = "Reports whether a bounding box is empty, valid and finite.",
    Keywords = "bounding box, aabb, box")]
public sealed class BoundsState : ValueNode
{
    public static void Run(BoundingBox bounds, out bool valid, out bool empty)
    {
        empty = bounds.IsEmpty;
        valid = !empty
            && float.IsFinite(bounds.Min.x) && float.IsFinite(bounds.Min.y) && float.IsFinite(bounds.Min.z)
            && float.IsFinite(bounds.Max.x) && float.IsFinite(bounds.Max.y) && float.IsFinite(bounds.Max.z);
    }
}
