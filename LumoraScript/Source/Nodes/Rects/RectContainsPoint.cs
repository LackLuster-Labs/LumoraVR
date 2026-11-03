// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A point on the edge counts as inside.
[Node("Math/Rects/Contains Point", Description = "Checks whether a point is inside or on the edge of a rectangle.",
    Keywords = "rectangle, inside, hit test")]
public sealed class RectContainsPoint : ValueNode
{
    [return: Port("contains")]
    public static bool Run(Rect rect, float2 point) => rect.Contains(point);
}
