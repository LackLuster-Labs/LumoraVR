// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A random color on the straight line from a to b: every channel mixed by the same random amount, from 0
// up to but not including 1. For an even mix of light, To Linear both ends first and To sRGB the result.
[Node("Math/Random/Colors/Random Lerp Color", Description = "A random color on the straight line from a to b: every channel mixed by the same random amount, from 0 up to but not including 1.")]
public sealed class RandomLerpColor : ReadNode
{
    [return: Port("color")]
    public static color Run(ScriptContextBase ctx, [Default(0, 0, 0, 1)] color a, [Default(1)] color b) => color.Lerp(a, b, ctx.Random.NextSingle());

    [return: Port("color")]
    public static colorHDR Run(ScriptContextBase ctx, [Default(0, 0, 0, 1)] colorHDR a, [Default(1)] colorHDR b)
        => colorHDR.Lerp(a, b, ctx.Random.NextSingle());
}
