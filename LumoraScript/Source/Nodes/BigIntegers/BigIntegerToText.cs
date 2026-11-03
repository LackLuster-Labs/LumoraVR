// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A big integer as text: decimal, or hexadecimal after 0x with hex on. Text To Big Integer reads either back.
[Node("Math/Big Integers/Big Integer To Text", Description = "A big integer as text: decimal, or hexadecimal after 0x with hex on.",
    Keywords = "bignum, arbitrary precision, large number")]
public sealed class BigIntegerToText : ValueNode
{
    [return: Port("text")]
    public static string Run(ScriptBigInt? value, bool hex = false)
        => hex ? (value ?? ScriptBigInt.Zero).ToHex() : (value ?? ScriptBigInt.Zero).ToString();
}
