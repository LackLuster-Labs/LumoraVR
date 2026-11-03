// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A code point as UTF-16 units. Past U+FFFF, a high and a low surrogate; up to U+FFFF, its one unit in
// high and the null character in low. Two null characters for a number below 0 or past U+10FFFF.
[Node("Strings/Characters/Code Point To Surrogates", Description = "A code point as UTF-16 units.",
    Keywords = "surrogate pair, utf16, split code point, utf32 to utf16")]
public sealed class ToUtf16 : ValueNode
{
    [return: Port("high")]
    public static char Run(int codePoint, out char low)
    {
        low = '\0';
        if ((uint)codePoint <= char.MaxValue)
            return (char)codePoint;
        if ((uint)codePoint > 0x10FFFF)
            return '\0';
        int offset = codePoint - 0x10000;
        low = (char)(0xDC00 + (offset & 0x3FF));
        return (char)(0xD800 + (offset >> 10));
    }
}
