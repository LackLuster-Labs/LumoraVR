// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// How many bits a big integer's size takes, its sign left out: 0 for 0, 8 for 255. A number holds at most
// ScriptBigInt.MaxBits.
[Node("Math/Big Integers/Bit Length", Description = "How many bits a big integer's size takes, its sign left out: 0 for 0, 8 for 255.",
    Keywords = "bignum, arbitrary precision, large number")]
public sealed class BitLength : ValueNode
{
    [return: Port("bits")]
    public static int Run(ScriptBigInt? value) => (int)ScriptBigInt.Bits(value);
}
