// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The value of the key at index in the order keys were first added, 0 first; the type's default outside
// that.
[Node("Data/Dictionaries/Value At", Description = "The value of the key at index in the order keys were first added, 0 first; the type's default outside that.",
    Keywords = "map, hashmap, key value")]
public sealed class DictValueAt<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("value")]
    public static TValue? Run(ScriptDictionary<TKey, TValue>? dictionary, int index) => dictionary == null ? default : dictionary.ValueAt(index);
}
