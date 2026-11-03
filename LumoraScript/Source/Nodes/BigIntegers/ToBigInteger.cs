// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A whole number as a big integer, and a double's whole part, cut toward zero; one that is not a finite number
// is 0.
[Node("Math/Big Integers/To Big Integer", Description = "A whole number as a big integer, and a double's whole part, cut toward zero; one that is not a finite number is 0.",
    Keywords = "bignum, arbitrary precision, large number")]
public sealed class ToBigInteger : ValueNode
{
    public static ScriptBigInt Run(int value) => ScriptBigInt.Of(value);
    public static ScriptBigInt Run(long value) => ScriptBigInt.Of(value);
    public static ScriptBigInt Run(ulong value) => ScriptBigInt.Of(value);
    public static ScriptBigInt Run(double value) => ScriptBigInt.FromDouble(value);
}
