// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Every key, in the order they were first added, as a new list. Later changes to the dictionary do not
// reach it. No dictionary gives an empty list.
[Node("Data/Dictionaries/Keys", Description = "Every key, in the order they were first added, as a new list.",
    Keywords = "map, hashmap, key value")]
public sealed class DictKeys<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("keys")]
    public static ScriptList<TKey> Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary)
    {
        var keys = new ScriptList<TKey>(ctx.Collections);
        if (dictionary == null)
            return keys;
        foreach (var key in dictionary.Keys)
            keys.Add(key);
        return keys;
    }
}
