// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// One random level, from 0 up to but not including 1, in red, green and blue; alpha 1.
[Node("Math/Random/Colors/Random Grayscale", Description = "One random level, from 0 up to but not including 1, in red, green and blue; alpha 1.",
    Keywords = "random color, random colour, random grey, random gray")]
public sealed class RandomGrayscale : ReadNode
{
    [return: Port("color")]
    public static color Run(ScriptContextBase ctx)
    {
        float level = ctx.Random.NextSingle();
        return new color(level, level, level, 1f);
    }
}
