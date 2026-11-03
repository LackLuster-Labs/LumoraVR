// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The chars of a list as one text, in order.
[Node("Strings/Collections/Characters To Text", Description = "The chars of a list as one text, in order.",
    Keywords = "implode, join characters")]
public sealed class CharactersToText : ReadNode
{
    [return: Port("text")]
    public static string Run(ScriptList<char>? characters)
    {
        if (characters == null || characters.Count == 0)
            return string.Empty;
        return string.Create(characters.Count, characters.Items, static (span, items) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = items[i];
        });
    }
}
