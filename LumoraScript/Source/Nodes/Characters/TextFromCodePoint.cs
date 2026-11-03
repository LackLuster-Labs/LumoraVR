// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Text;

namespace Lumora.Script;

// The character with that Unicode number, as text; past U+FFFF that is two UTF-16 units. Empty for a
// number that names no character: below 0, past U+10FFFF, or a surrogate half.
[Node("Strings/Characters/Text From Code Point", Description = "The character with that Unicode number, as text; past U+FFFF that is two UTF-16 units.",
    Keywords = "utf32, unicode, chr, emoji, character from number")]
public sealed class TextFromCodePoint : ValueNode
{
    [return: Port("text")]
    public static string Run(int codePoint) => Rune.IsValid(codePoint) ? char.ConvertFromUtf32(codePoint) : string.Empty;
}
