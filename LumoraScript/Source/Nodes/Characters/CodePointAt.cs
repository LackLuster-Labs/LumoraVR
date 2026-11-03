// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The Unicode number of the character at index. Where a surrogate pair starts, the one character the
// pair makes; any other surrogate half, the second of a pair included, reads as itself. 0 when index is
// outside the text.
[Node("Strings/Characters/Code Point At", Description = "The Unicode number of the character at index.",
    Keywords = "utf32, unicode, char code at, rune")]
public sealed class CodePointAt : ValueNode
{
    [return: Port("codePoint")]
    public static int Run(string? text, int index)
    {
        if (text == null || (uint)index >= (uint)text.Length)
            return 0;
        char unit = text[index];
        if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            return char.ConvertToUtf32(unit, text[index + 1]);
        return unit;
    }
}
