// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The array with value put in at index, 0 first, which may be one past the end. inserted is false, and the
// array as it was, for any other index. No array starts from an empty one.
[Node("Data/JSON/Arrays/Insert", Description = "The array with value put in at index, 0 first, which may be one past the end. inserted is false, and the array as it was, for any other index.")]
public sealed class JsonInsert<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? array, int index, T value, out bool inserted)
    {
        var start = array ?? ScriptJsonArray.Empty;
        inserted = (uint)index <= (uint)start.Count
                   && start.TryInsert(ctx.Collections, index, ScriptJsonConvert.From(ctx.Collections, value), out start);
        return start;
    }
}
