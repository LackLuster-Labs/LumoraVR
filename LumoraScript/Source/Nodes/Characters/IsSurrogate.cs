// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is one half of a surrogate pair, the two UTF-16 units a character past
// U+FFFF takes, such as an emoji.
[Node("Strings/Characters/Is Surrogate", Description = "Whether the character is one half of a surrogate pair, the two UTF-16 units a character past U+FFFF takes, such as an emoji.")]
public sealed class IsSurrogate : ValueNode
{
    public static bool Run(char character) => char.IsSurrogate(character);
}
