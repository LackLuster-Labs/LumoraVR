// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The rect between two opposite corners, which may come in either order.
[Node("Math/Rects/Rect From Min Max", Description = "The rect between two opposite corners, which may come in either order.",
    Keywords = "rectangle")]
public sealed class RectFromMinMax : ValueNode
{
    [return: Port("rect")]
    public static Rect Run(float2 min, float2 max) => Rect.FromMinMax(float2.Min(min, max), float2.Max(min, max));
}
