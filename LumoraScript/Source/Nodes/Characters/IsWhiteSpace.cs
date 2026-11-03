// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is white space: spaces, tabs and line breaks.
[Node("Strings/Characters/Is White Space", Description = "Whether the character is white space: spaces, tabs and line breaks.")]
public sealed class IsWhiteSpace : ValueNode
{
    public static bool Run(char character) => char.IsWhiteSpace(character);
}
