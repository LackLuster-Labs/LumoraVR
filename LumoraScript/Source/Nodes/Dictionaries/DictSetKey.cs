// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Gives key this value, adding the key when it is new; a key already there keeps its place in the order.
// A key that holds nothing fails, as no dictionary does.
[Node("Data/Dictionaries/Set Key", Description = "Gives key this value, adding the key when it is new; a key already there keeps its place in the order.",
    Keywords = "put, store, map set, map, hashmap, key value")]
public sealed class DictSetKey<TKey, TValue> : ActionNode where TKey : notnull
{
    public static void Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary, TKey? key, TValue value)
    {
        if (dictionary == null)
        {
            ctx.Fail("no dictionary");
            return;
        }
        if (key is null)
        {
            ctx.Fail("a dictionary key cannot be null");
            return;
        }
        dictionary.Set(key, value);
    }
}
