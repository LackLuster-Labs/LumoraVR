// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The array with the item at index replaced by value; replaced is false, and the array as it was, when the
// index is outside it.
[Node("Data/JSON/Arrays/Set At", Description = "The array with the item at index replaced by value; replaced is false, and the array as it was, when the index is outside it.")]
public sealed class JsonSetAt<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? array, int index, T value, out bool replaced)
    {
        var start = array ?? ScriptJsonArray.Empty;
        replaced = (uint)index < (uint)start.Count
                   && start.TrySet(ctx.Collections, index, ScriptJsonConvert.From(ctx.Collections, value), out start);
        return start;
    }
}
