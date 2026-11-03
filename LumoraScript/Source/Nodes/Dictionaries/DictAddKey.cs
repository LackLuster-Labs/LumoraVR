// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Adds key with this value only when the dictionary does not have it yet; added is false, and the value
// already there stays, when it does. A key that holds nothing fails, as no dictionary does.
[Node("Data/Dictionaries/Add Key", Description = "Adds key with this value only when the dictionary does not have it yet; added is false, and the value already there stays, when it does.",
    Keywords = "insert, put if absent, map, hashmap, key value")]
public sealed class DictAddKey<TKey, TValue> : ActionNode where TKey : notnull
{
    [return: Port("added")]
    public static bool Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary, TKey? key, TValue value)
    {
        if (dictionary == null)
        {
            ctx.Fail("no dictionary");
            return false;
        }
        if (key is null)
        {
            ctx.Fail("a dictionary key cannot be null");
            return false;
        }
        return dictionary.Add(key, value);
    }
}
