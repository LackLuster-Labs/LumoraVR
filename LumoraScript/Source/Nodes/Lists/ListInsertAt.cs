// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Puts item at index and moves the rest along; an index equal to the count adds at the end. Anywhere else
// nothing changes, inserted is false and the call fails, as it does without a list.
[Node("Data/Lists/Insert At", Description = "Puts item at index and moves the rest along; an index equal to the count adds at the end.",
    Keywords = "insert, array")]
public sealed class ListInsertAt<T> : ActionNode
{
    [return: Port("inserted")]
    public static bool Run(ScriptContextBase ctx, ScriptList<T>? list, int index, T item)
    {
        if (list == null)
        {
            ctx.Fail("no list");
            return false;
        }
        if (list.Insert(index, item))
            return true;
        ctx.Fail($"index {index} is outside the list of {list.Count}");
        return false;
    }
}
