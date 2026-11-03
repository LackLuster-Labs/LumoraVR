// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Takes out the first item equal to item: the same text exactly, the same numbers, the same object. An item
// that was not there is no failure, since it is out of the list either way; no list is.
[Node("Data/Lists/Remove", Description = "Takes out the first item equal to item: the same text exactly, the same numbers, the same object.",
    Keywords = "delete, erase")]
public sealed class ListRemove<T> : ActionNode
{
    [return: Port("removed")]
    public static bool Run(ScriptContextBase ctx, ScriptList<T>? list, T item)
    {
        if (list != null)
            return list.Remove(item);
        ctx.Fail("no list");
        return false;
    }
}
