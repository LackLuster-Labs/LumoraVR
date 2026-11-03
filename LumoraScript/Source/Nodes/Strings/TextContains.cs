// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Exact and case sensitive. An empty part is always found.
[Node("Strings/Contains", Description = "Checks whether text contains the given text. Matching is case-sensitive.",
    Keywords = "includes, has, find in text")]
public sealed class TextContains : ValueNode
{
    public static bool Run(string? text, string? part)
        => (text ?? string.Empty).Contains(part ?? string.Empty, StringComparison.Ordinal);
}
