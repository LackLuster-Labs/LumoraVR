// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Text read as a big integer: decimal with a sign in front if any, or hexadecimal after 0x, spaces round it and
// _ between digits allowed. parsed is false, and the value 0, when the text is not one or is too long to hold.
[Node("Math/Big Integers/Text To Big Integer", Description = "Reads a large integer from decimal or hexadecimal text. Reports whether parsing succeeded.",
    Keywords = "bignum, arbitrary precision, large number")]
public sealed class TextToBigInteger : ValueNode
{
    [return: Port("value")]
    public static ScriptBigInt Run(string? text, out bool parsed)
    {
        parsed = ScriptBigInt.TryParse(text, out var number);
        return number;
    }
}
