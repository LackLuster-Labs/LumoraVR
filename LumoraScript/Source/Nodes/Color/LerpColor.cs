// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

[Node("Color/Lerp Color", Description = "Blends between two colours using a blend amount, with 0 giving the first and 1 the second.",
    Keywords = "mix, blend colors, gradient, interpolate")]
public sealed class LerpColor : ValueNode
{
    [return: Port("color")]
    public static color Run([Default(0, 0, 0, 1)] color a, [Default(1)] color b, float t) => color.Lerp(a, b, t);
}
