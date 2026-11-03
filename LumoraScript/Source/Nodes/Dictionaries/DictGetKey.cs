// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The value under key; found is false, and the value the type's default, when there is none.
[Node("Data/Dictionaries/Get Key", Description = "The value under key; found is false, and the value the type's default, when there is none.",
    Keywords = "lookup, get value, map, hashmap, key value")]
public sealed class DictGetKey<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("value")]
    public static TValue? Run(ScriptDictionary<TKey, TValue>? dictionary, TKey? key, out bool found)
    {
        TValue? value = default;
        found = dictionary != null && dictionary.TryGet(key, out value);
        return value;
    }
}
