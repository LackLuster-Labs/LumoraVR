// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// Where find first starts in text, or -1. Exact and case sensitive. With a start, the search begins there, or
// runs back from there when fromEnd. A start below 0, as an unwired one is, takes the whole text from its first
// character, or from its last when fromEnd, and so does a start past the end when fromEnd. Then -1 also when
// find is empty or a forward start is past the end.
[Node("Strings/Index Of", Description = "Where find first starts in text, or -1. With fromEnd, where it last starts, searching back from start or from the end.",
    Keywords = "find, search, position, locate, last index of")]
public sealed class IndexOf : ValueNode
{
    [return: Port("index")]
    public static int Run(string? text, string? find) => (text ?? string.Empty).IndexOf(find ?? string.Empty, StringComparison.Ordinal);

    [return: Port("index")]
    public static int Run(string? text, string? find, int start = -1, bool fromEnd = false)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find))
            return -1;
        if (fromEnd)
            return text.LastIndexOf(find, start < 0 || start >= text.Length ? text.Length - 1 : start, StringComparison.Ordinal);
        return start >= text.Length ? -1 : text.IndexOf(find, Math.Max(start, 0), StringComparison.Ordinal);
    }
}
