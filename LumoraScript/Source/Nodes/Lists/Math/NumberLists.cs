// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Script;

// What the number list nodes share: two lists taken pair by pair while both still have one, so the
// result is as long as the shorter.
internal static class NumberLists
{
    public static ScriptList<float> Pairwise(ScriptContextBase ctx, ScriptList<float>? a, ScriptList<float>? b, Func<float, float, float> op)
    {
        var result = new ScriptList<float>(ctx.Collections);
        if (a == null || b == null)
            return result;
        int count = Math.Min(a.Count, b.Count);
        var left = a.Items;
        var right = b.Items;
        for (int i = 0; i < count; i++)
            result.Add(op(left[i], right[i]));
        return result;
    }
}
