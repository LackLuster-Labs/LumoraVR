// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The largest whole number that divides both, never below 0: 12 and 18 give 6. Signs do not matter. Any
// number divides 0, so 0 and n give n, and 0 and 0 give 0. The one answer a signed type cannot hold, 2^31
// for int (from the smallest int with itself or 0), comes back as that smallest number, the way Abs does.
[Node("Math/Greatest Common Divisor", Description = "The largest whole number that divides both, never below 0: 12 and 18 give 6.",
    Keywords = "gcd, hcf, highest common factor")]
public sealed class GreatestCommonDivisor : ValueNode
{
    public static int Run(int a, int b) => unchecked((int)Of(Magnitude(a), Magnitude(b)));
    public static long Run(long a, long b) => unchecked((long)Of(Magnitude(a), Magnitude(b)));
    public static uint Run(uint a, uint b) => (uint)Of(a, b);
    public static ulong Run(ulong a, ulong b) => Of(a, b);

    internal static ulong Of(ulong a, ulong b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    // How far from 0, whole even for the smallest long.
    internal static ulong Magnitude(long value) => value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;

    public static ScriptBigInt Run(ScriptBigInt? a, ScriptBigInt? b) => ScriptBigInt.GreatestCommonDivisor(a, b);
}
