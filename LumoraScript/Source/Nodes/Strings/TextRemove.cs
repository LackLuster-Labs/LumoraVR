// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// text with count characters taken out from start, cut short at the end of the text rather than failing.
// Left alone, count takes out everything from start on.
[Node("Strings/Remove", Description = "Text with count characters taken out from start, cut short at the end of the text rather than failing. Left alone, count takes out everything from start on.",
    Keywords = "delete, erase, cut out")]
public sealed class TextRemove : ValueNode
{
    public static string Run(string? text, int start, int count = int.MaxValue)
    {
        text ??= string.Empty;
        start = Math.Clamp(start, 0, text.Length);
        count = Math.Clamp(count, 0, text.Length - start);
        return text.Remove(start, count);
    }
}
