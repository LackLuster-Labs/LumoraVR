// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// The smallest whole number both divide into, never below 0: 4 and 6 give 12. Signs do not matter. Either
// one 0 gives 0, and so does a multiple too big for the type, which would otherwise wrap round into a number
// that is no multiple at all.
[Node("Math/Least Common Multiple", Description = "The smallest whole number both divide into, never below 0: 4 and 6 give 12.",
    Keywords = "lcm, lowest common multiple")]
public sealed class LeastCommonMultiple : ValueNode
{
    public static int Run(int a, int b)
        => (int)Of(GreatestCommonDivisor.Magnitude(a), GreatestCommonDivisor.Magnitude(b), int.MaxValue);

    public static long Run(long a, long b)
        => (long)Of(GreatestCommonDivisor.Magnitude(a), GreatestCommonDivisor.Magnitude(b), long.MaxValue);

    public static uint Run(uint a, uint b) => (uint)Of(a, b, uint.MaxValue);
    public static ulong Run(ulong a, ulong b) => (ulong)Of(a, b, ulong.MaxValue);

    // The multiple, or 0 when either is 0 or the multiple is past most.
    private static UInt128 Of(ulong a, ulong b, ulong most)
    {
        if (a == 0 || b == 0)
            return 0;
        UInt128 multiple = (UInt128)(a / GreatestCommonDivisor.Of(a, b)) * b;
        return multiple > most ? 0 : multiple;
    }
}
