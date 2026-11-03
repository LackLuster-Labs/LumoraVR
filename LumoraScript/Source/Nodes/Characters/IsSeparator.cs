// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a space, line or paragraph separator. Tabs and line breaks are control
// characters, not separators.
[Node("Strings/Characters/Is Separator", Description = "Whether the character is a space, line or paragraph separator.")]
public sealed class IsSeparator : ValueNode
{
    public static bool Run(char character) => char.IsSeparator(character);
}
