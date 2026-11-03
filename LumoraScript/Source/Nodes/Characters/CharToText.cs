// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The character as one-character text. To Text writes the typed form instead, "space" for a space.
[Node("Strings/Characters/Char To Text", Description = "The character as one-character text.")]
public sealed class CharToText : ValueNode
{
    public static string Run(char character) => character.ToString();
}
