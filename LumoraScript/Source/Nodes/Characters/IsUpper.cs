// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is an upper case letter.
[Node("Strings/Characters/Is Upper", Description = "Whether the character is an upper case letter.")]
public sealed class IsUpper : ValueNode
{
    public static bool Run(char character) => char.IsUpper(character);
}
