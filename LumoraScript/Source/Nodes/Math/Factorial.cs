// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// value! as a double, 1 * 2 * ... * value: 5 gives 120, and 0 and 1 give 1. Past 170 it is too big for a
// double and gives infinity; below 0 there is no factorial and it gives NaN.
[Node("Math/Factorial", Description = "Value! as a double, 1 * 2 * ... * value: 5 gives 120, and 0 and 1 give 1.",
    Keywords = "permutations")]
public sealed class Factorial : ValueNode
{
    private const int Largest = 170;

    public static double Run(int value)
    {
        if (value < 0)
            return double.NaN;
        if (value > Largest)
            return double.PositiveInfinity;
        double product = 1d;
        for (int i = 2; i <= value; i++)
            product *= i;
        return product;
    }
}
