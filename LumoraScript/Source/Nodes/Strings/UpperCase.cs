// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

[Node("Strings/Upper Case", Description = "Converts text to uppercase.",
    Keywords = "uppercase, capitals")]
public sealed class UpperCase : ValueNode
{
    public static string Run(string? text) => (text ?? string.Empty).ToUpperInvariant();
}
