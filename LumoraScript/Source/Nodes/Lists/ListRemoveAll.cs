// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Takes out every item equal to item; removed is how many went. None is no failure; no list is.
[Node("Data/Lists/Remove All", Description = "Takes out every item equal to item; removed is how many went.",
    Keywords = "delete all, erase all, filter out")]
public sealed class ListRemoveAll<T> : ActionNode
{
    [return: Port("removed")]
    public static int Run(ScriptContextBase ctx, ScriptList<T>? list, T item)
    {
        if (list != null)
            return list.RemoveAll(item);
        ctx.Fail("no list");
        return 0;
    }
}
