// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a lower case letter.
[Node("Strings/Characters/Is Lower", Description = "Whether the character is a lower case letter.")]
public sealed class IsLower : ValueNode
{
    public static bool Run(char character) => char.IsLower(character);
}
