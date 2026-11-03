// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Invariant, the same on every machine. A character with no upper case form comes back as it is.
[Node("Strings/Characters/To Upper", Description = "Converts a character to uppercase using the same rules on every machine.")]
public sealed class CharToUpper : ValueNode
{
    public static char Run(char character) => char.ToUpperInvariant(character);
}
