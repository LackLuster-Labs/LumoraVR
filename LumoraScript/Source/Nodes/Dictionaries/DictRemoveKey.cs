// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Takes out key and its value; removed is false when the dictionary did not have it, which is no failure,
// since the key is gone either way. No dictionary fails.
[Node("Data/Dictionaries/Remove Key", Description = "Takes out key and its value; removed is false when the dictionary did not have it, which is no failure, since the key is gone either way.",
    Keywords = "delete key, erase key, map, hashmap, key value")]
public sealed class DictRemoveKey<TKey, TValue> : ActionNode where TKey : notnull
{
    [return: Port("removed")]
    public static bool Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary, TKey? key)
    {
        if (dictionary != null)
            return dictionary.Remove(key);
        ctx.Fail("no dictionary");
        return false;
    }
}
