// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Invariant, the same on every machine. A character with no lower case form comes back as it is.
[Node("Strings/Characters/To Lower", Description = "Converts a character to lowercase using the same rules on every machine.")]
public sealed class CharToLower : ValueNode
{
    public static char Run(char character) => char.ToLowerInvariant(character);
}
