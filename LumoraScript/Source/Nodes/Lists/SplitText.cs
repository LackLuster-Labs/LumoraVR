// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// text cut at every separator, exact and case sensitive, empty pieces kept: "a,,b" by "," gives "a", ""
// and "b", and empty text one empty piece. An empty separator gives every character on its own, a
// character that takes two chars kept whole, and empty text no pieces.
[Node("Strings/Collections/Split Text", Description = "Text cut at every separator, exact and case sensitive, empty pieces kept: \"a,,b\" by \",\" gives \"a\", \"\" and \"b\", and empty text one empty piece.",
    Keywords = "explode, tokenize, string to list")]
public sealed class SplitText : ReadNode
{
    [return: Port("parts")]
    public static ScriptList<string> Run(ScriptContextBase ctx, string? text, string? separator)
    {
        text ??= string.Empty;
        var parts = new ScriptList<string>(ctx.Collections);
        if (string.IsNullOrEmpty(separator))
        {
            for (int i = 0; i < text.Length;)
            {
                int length = char.IsSurrogatePair(text, i) ? 2 : 1;
                parts.Add(text.Substring(i, length));
                i += length;
            }
            return parts;
        }

        int start = 0;
        for (int at = text.IndexOf(separator, StringComparison.Ordinal); at >= 0; at = text.IndexOf(separator, start, StringComparison.Ordinal))
        {
            parts.Add(text[start..at]);
            start = at + separator.Length;
        }
        parts.Add(text[start..]);
        return parts;
    }
}
