// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a number of any kind: decimal digits, and also fractions, superscripts and
// numerals such as ½, ² and Ⅻ.
[Node("Strings/Characters/Is Number", Description = "Whether the character is a number of any kind: decimal digits, and also fractions, superscripts and numerals such as ½, ² and Ⅻ.")]
public sealed class IsNumber : ValueNode
{
    public static bool Run(char character) => char.IsNumber(character);
}
