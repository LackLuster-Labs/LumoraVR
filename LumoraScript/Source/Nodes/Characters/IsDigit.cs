// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a decimal digit, 0 to 9 in any script.
[Node("Strings/Characters/Is Digit", Description = "Whether the character is a decimal digit, 0 to 9 in any script.")]
public sealed class IsDigit : ValueNode
{
    public static bool Run(char character) => char.IsDigit(character);
}
