// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A new list of count items from start on, 0 first; what runs past either end is left out, so a slice can
// come back shorter. A count below 0 takes everything from start.
[Node("Data/Lists/Slice", Description = "A new list of count items from start on, 0 first; what runs past either end is left out, so a slice can come back shorter.",
    Keywords = "sublist, subarray, range, take, skip")]
public sealed class ListSlice<T> : ReadNode
{
    [return: Port("slice")]
    public static ScriptList<T> Run(ScriptContextBase ctx, ScriptList<T>? list, int start, int count = -1)
    {
        var slice = new ScriptList<T>(ctx.Collections);
        if (list == null)
            return slice;
        var items = list.Items;
        int from = System.Math.Max(0, start);
        int to = count < 0 ? items.Count : (int)System.Math.Min((long)start + count, items.Count);
        for (int i = from; i < to; i++)
            slice.Add(items[i]);
        return slice;
    }
}
