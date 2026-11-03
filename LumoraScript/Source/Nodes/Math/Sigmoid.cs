// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The S curve 1 / (1 + base^-value): near 0 far below 0, 0.5 at 0, near 1 far above. Base defaults to e; a
// bigger base makes a steeper S, one between 0 and 1 turns it round, and 1 flattens it to 0.5. A base of 0 or
// below has no powers to speak of and gives NaN.
[Node("Math/Shaping/Sigmoid", Description = "The S curve 1 / (1 + base^-value): near 0 far below 0, 0.5 at 0, near 1 far above.",
    Keywords = "logistic, s curve")]
public sealed class Sigmoid : ValueNode
{
    [Gpu("1.0 / (1.0 + pow({base}, -{value}))")]
    public static float Run(float value, float @base = MathF.E) => @base > 0f ? 1f / (1f + MathF.Pow(@base, -value)) : float.NaN;
    public static double Run(double value, double @base = Math.E) => @base > 0d ? 1d / (1d + Math.Pow(@base, -value)) : double.NaN;
}
