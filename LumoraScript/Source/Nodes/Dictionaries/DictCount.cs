// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// How many keys a dictionary holds; 0 for no dictionary.
[Node("Data/Dictionaries/Count", Description = "How many keys a dictionary holds; 0 for no dictionary.",
    Keywords = "size, length, map, hashmap, key value")]
public sealed class DictCount<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("count")]
    public static int Run(ScriptDictionary<TKey, TValue>? dictionary) => dictionary?.Count ?? 0;
}
