// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The array without the item at index; removed is false, and the array as it was, when the index is
// outside it.
[Node("Data/JSON/Arrays/Remove At", Description = "The array without the item at index; removed is false, and the array as it was, when the index is outside it.")]
public sealed class JsonRemoveAt : ReadNode
{
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? array, int index, out bool removed)
    {
        var start = array ?? ScriptJsonArray.Empty;
        removed = start.TryRemoveAt(ctx.Collections, index, out start);
        return start;
    }
}
