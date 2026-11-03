// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the character is a symbol: math, currency, modifier and other symbols such as + $ ^ ©.
[Node("Strings/Characters/Is Symbol", Description = "Whether the character is a symbol: math, currency, modifier and other symbols such as + $ ^ ©.")]
public sealed class IsSymbol : ValueNode
{
    public static bool Run(char character) => char.IsSymbol(character);
}
