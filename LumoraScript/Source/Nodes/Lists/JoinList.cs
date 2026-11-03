// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The list's text end to end with separator between each two. An item that holds nothing reads as empty
// text.
[Node("Strings/Collections/Join List", Description = "The list's text end to end with separator between each two.",
    Keywords = "implode, concatenate, glue, string")]
public sealed class JoinList : ReadNode
{
    [return: Port("text")]
    public static string Run(ScriptList<string>? list, string? separator)
    {
        if (list == null || list.Count == 0)
            return string.Empty;
        return string.Join(separator ?? string.Empty, list.Items);
    }
}
