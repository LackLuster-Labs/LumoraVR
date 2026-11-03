// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Puts item in place of the one at index. Outside the list nothing changes, replaced is false and the call
// fails, as it does without a list.
[Node("Data/Lists/Set At", Description = "Puts item in place of the one at index.")]
public sealed class ListSetAt<T> : ActionNode
{
    [return: Port("replaced")]
    public static bool Run(ScriptContextBase ctx, ScriptList<T>? list, int index, T item)
    {
        if (list == null)
        {
            ctx.Fail("no list");
            return false;
        }
        if (list.Set(index, item))
            return true;
        ctx.Fail($"index {index} is outside the list of {list.Count}");
        return false;
    }
}
