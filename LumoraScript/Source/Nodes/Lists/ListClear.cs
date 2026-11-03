// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Empties the list and gives the room its items took back to the budget. No list fails.
[Node("Data/Lists/Clear", Description = "Removes every item from the list.",
    Keywords = "empty, reset, remove all")]
public sealed class ListClear<T> : ActionNode
{
    public static void Run(ScriptContextBase ctx, ScriptList<T>? list)
    {
        if (list == null)
            ctx.Fail("no list");
        else
            list.Clear();
    }
}
