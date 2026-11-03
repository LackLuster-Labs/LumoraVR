// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Without the spaces and line breaks at either end.
[Node("Strings/Trim", Description = "Without the spaces and line breaks at either end.",
    Keywords = "strip, whitespace, spaces")]
public sealed class Trim : ValueNode
{
    public static string Run(string? text) => (text ?? string.Empty).Trim();
}
