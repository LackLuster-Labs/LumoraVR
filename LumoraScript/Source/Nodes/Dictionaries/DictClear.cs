// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Empties the dictionary and gives the room its keys and values took back to the budget. No dictionary
// fails.
[Node("Data/Dictionaries/Clear", Description = "Removes every key and value from the dictionary.",
    Keywords = "map, hashmap, key value")]
public sealed class DictClear<TKey, TValue> : ActionNode where TKey : notnull
{
    public static void Run(ScriptContextBase ctx, ScriptDictionary<TKey, TValue>? dictionary)
    {
        if (dictionary == null)
            ctx.Fail("no dictionary");
        else
            dictionary.Clear();
    }
}
