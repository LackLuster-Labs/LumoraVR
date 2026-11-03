// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

[Node("Strings/Lower Case", Description = "Converts text to lowercase.",
    Keywords = "lowercase, small letters")]
public sealed class LowerCase : ValueNode
{
    public static string Run(string? text) => (text ?? string.Empty).ToLowerInvariant();
}
