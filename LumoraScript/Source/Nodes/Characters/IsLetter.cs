// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a letter, in any script.
[Node("Strings/Characters/Is Letter", Description = "Whether the character is a letter, in any script.")]
public sealed class IsLetter : ValueNode
{
    public static bool Run(char character) => char.IsLetter(character);
}
