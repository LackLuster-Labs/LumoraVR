// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Every value, in the order the keys were first added, as a new list. Later changes to the dictionary do
// not reach it. No dictionary gives an empty list.
[Node("Data/Dictionaries/Values", Description = "Every value, in the order the keys were first added, as a new list.",
    Keywords = "map, hashmap, key value")]
public sealed class DictValues<TKey, TValue> : ReadNode where TKey : notnull
{
    [return: Port("values")]
    public static ScriptList<TValue> Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary)
    {
        var values = new ScriptList<TValue>(ctx.Collections);
        if (dictionary == null)
            return values;
        for (int i = 0; i < dictionary.Count; i++)
            values.Add(dictionary.ValueAt(i)!);
        return values;
    }
}
