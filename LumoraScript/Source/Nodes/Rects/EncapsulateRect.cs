// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The smallest rect holding both. A rect with no area counts as nothing, so an unset one does not pull
// the union to the origin. A point always counts: the rect grows just enough to take it in.
[Variadic]
[Node("Math/Rects/Encapsulate", Description = "The smallest rect holding both.",
    Keywords = "rectangle, union, merge, combine")]
public sealed class EncapsulateRect : ValueNode
{
    [return: Port("union")] public static Rect Run(Rect a, Rect b) => a.Encapsulate(b);
    [return: Port("union")] public static Rect Run(Rect rect, float2 point) => rect.Encapsulate(point);
}
