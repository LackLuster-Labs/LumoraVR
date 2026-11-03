// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A new list holding value count times: silence to pad samples with, a flat gain to shape. A count below 1
// gives an empty list, and an unwired one is 1; a list can hold up to 65536 this way.
[Node("Data/Lists/Fill", Description = "A new list holding value count times: silence to pad samples with, a flat gain to shape.",
    Keywords = "repeat, filled list, array of, pad")]
public sealed class ListFill<T> : ReadNode
{
    public const int MaxCount = 65536;

    [return: Port("list")]
    public static ScriptList<T> Run(ScriptContextBase ctx, [Default(1)] int count, T value)
    {
        var list = new ScriptList<T>(ctx.Collections);
        int n = System.Math.Clamp(count, 0, MaxCount);
        for (int i = 0; i < n; i++)
            list.Add(value);
        return list;
    }
}
