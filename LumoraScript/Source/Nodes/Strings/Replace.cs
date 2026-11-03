// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Every find in text swapped for with. Exact and case sensitive.
[Node("Strings/Replace", Description = "Every find in text swapped for with.",
    Keywords = "substitute, swap, replace all")]
public sealed class Replace : ValueNode
{
    public static string Run(string? text, string? find, string? with)
    {
        text ??= string.Empty;
        if (string.IsNullOrEmpty(find))
            return text;
        return text.Replace(find, with ?? string.Empty, StringComparison.Ordinal);
    }
}
