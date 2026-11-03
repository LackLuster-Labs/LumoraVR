// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Takes out the item at index and moves the rest back. Outside the list nothing changes, removed is false
// and the call fails, as it does without a list.
[Node("Data/Lists/Remove At", Description = "Takes out the item at index and moves the rest back.",
    Keywords = "delete at, erase at")]
public sealed class ListRemoveAt<T> : ActionNode
{
    [return: Port("removed")]
    public static bool Run(ScriptContextBase ctx, ScriptList<T>? list, int index)
    {
        if (list == null)
        {
            ctx.Fail("no list");
            return false;
        }
        if (list.RemoveAt(index))
            return true;
        ctx.Fail($"index {index} is outside the list of {list.Count}");
        return false;
    }
}
