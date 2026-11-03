// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The character's UTF-16 unit as a number, 0 to 65535.
[Node("Strings/Characters/Char To Code", Description = "The character's UTF-16 unit as a number, 0 to 65535.",
    Keywords = "ascii, ord, char code, character code, utf16, unicode number")]
public sealed class CharToCode : ValueNode
{
    [return: Port("code")]
    public static int Run(char character) => character;
}
