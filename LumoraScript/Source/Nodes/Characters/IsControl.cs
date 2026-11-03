// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a control character, such as a tab, a line break or the null character.
[Node("Strings/Characters/Is Control", Description = "Whether the character is a control character, such as a tab, a line break or the null character.")]
public sealed class IsControl : ValueNode
{
    public static bool Run(char character) => char.IsControl(character);
}
