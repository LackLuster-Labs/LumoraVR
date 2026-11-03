// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The key at index in the order keys were first added, 0 first; the type's default outside that.
[Node("Data/Dictionaries/Key At", Description = "The key at index in the order keys were first added, 0 first; the type's default outside that.",
    Keywords = "map, hashmap, key value")]
public sealed class DictKeyAt<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("key")]
    public static TKey? Run(ScriptDictionary<TKey, TValue>? dictionary, int index) => dictionary == null ? default : dictionary.KeyAt(index);
}
