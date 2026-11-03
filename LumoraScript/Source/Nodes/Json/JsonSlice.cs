// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// count items from start, as a new array; the part of that range outside the array is left out, so a start
// of -2 with a count of 3 is the first item alone. Left alone, count takes everything from start on.
[Node("Data/JSON/Arrays/Slice", Description = "Count items from start, as a new array; the part of that range outside the array is left out. Left alone, count takes everything from start on.")]
public sealed class JsonSlice : ReadNode
{
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? array, int start, int count = int.MaxValue)
    {
        if (array == null)
            return ScriptJsonArray.Empty;
        long end = System.Math.Min((long)start + System.Math.Max(count, 0), array.Count);
        int from = System.Math.Max(start, 0);
        return array.Slice(ctx.Collections, from, (int)System.Math.Max(end - from, 0L));
    }
}
