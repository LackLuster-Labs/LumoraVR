// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The code point a high and a low surrogate make together. When the two are not a pair, high's own
// number, so a character up to U+FFFF with the null character as low reads back as itself.
[Node("Strings/Characters/Surrogates To Code Point", Description = "The code point a high and a low surrogate make together.",
    Keywords = "surrogate pair, utf16, utf32, combine surrogates")]
public sealed class FromUtf16 : ValueNode
{
    [return: Port("codePoint")]
    public static int Run(char high, char low) => char.IsSurrogatePair(high, low) ? char.ConvertToUtf32(high, low) : high;
}
