// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The character at index, 0 first. Indexes count UTF-16 units, so an emoji takes two. The null
// character when index is outside the text.
[Node("Strings/Characters/Get Character", Description = "The character at index, 0 first.",
    Keywords = "char at, character at, letter at")]
public sealed class GetCharacter : ValueNode
{
    [return: Port("character")]
    public static char Run(string? text, int index)
        => text != null && (uint)index < (uint)text.Length ? text[index] : '\0';
}
