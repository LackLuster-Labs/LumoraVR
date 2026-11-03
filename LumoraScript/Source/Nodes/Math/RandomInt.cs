// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// From min up to but not including max. Given the larger first, the two swap, so the range still holds; min
// when they are equal.
[Node("Math/Random/Random Int", Description = "From min up to but not including max, the two swapped when min is the larger; min when they are equal.",
    Keywords = "random number, random integer, dice, random whole number")]
public sealed class RandomInt : ReadNode
{
    public static int Run(ScriptContextBase ctx, int min = 0, int max = 100)
    {
        if (max < min)
            (min, max) = (max, min);
        return max == min ? min : ctx.Random.Next(min, max);
    }
}
