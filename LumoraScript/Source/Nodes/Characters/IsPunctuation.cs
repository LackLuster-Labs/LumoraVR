// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is punctuation, such as stops, commas, dashes, brackets and quotes.
[Node("Strings/Characters/Is Punctuation", Description = "Whether the character is punctuation, such as stops, commas, dashes, brackets and quotes.")]
public sealed class IsPunctuation : ValueNode
{
    public static bool Run(char character) => char.IsPunctuation(character);
}
