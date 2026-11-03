// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// An empty dictionary. It is in the flow, so every node after it that reads its dictionary reads this
// one.
[Node("Data/Dictionaries/New Dictionary", Description = "An empty dictionary.",
    Keywords = "create map, hashmap, lookup table, map, key value")]
public sealed class NewDictionary<TKey, TValue> : ActionNode where TKey : notnull
{
    [return: Port("dictionary")]
    public static ScriptDictionary<TKey, TValue> Run(ScriptContextBase ctx) => new(ctx.Collections);
}
