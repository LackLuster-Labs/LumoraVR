// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Red, green and blue each random, from 0 up to but not including 1; alpha 1.
[Node("Math/Random/Colors/Random RGB", Description = "Red, green and blue each random, from 0 up to but not including 1; alpha 1.",
    Keywords = "random color, random colour")]
public sealed class RandomRgb : ReadNode
{
    [return: Port("color")]
    public static color Run(ScriptContextBase ctx)
    {
        var random = ctx.Random;
        return new color(random.NextSingle(), random.NextSingle(), random.NextSingle(), 1f);
    }
}
