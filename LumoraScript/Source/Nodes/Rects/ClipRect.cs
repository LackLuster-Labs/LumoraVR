// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The part of rect inside mask. Rects that do not meet give the zero rect; rects that only touch give
// the shared edge, with no width or no height.
[Node("Math/Rects/Clip", Description = "The part of rect inside mask.",
    Keywords = "rectangle, intersection, crop, intersect")]
public sealed class ClipRect : ValueNode
{
    [return: Port("clipped")]
    public static Rect Run(Rect rect, Rect mask) => rect.Intersection(mask);
}
