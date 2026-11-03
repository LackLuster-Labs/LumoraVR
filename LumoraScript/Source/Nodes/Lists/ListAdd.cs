// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Puts item at the end. Without a list nothing happens, and the call fails.
[Node("Data/Lists/Add", Description = "Puts item at the end.",
    Keywords = "append, push, array")]
public sealed class ListAdd<T> : ActionNode
{
    public static void Run(ScriptContextBase ctx, ScriptList<T>? list, T item)
    {
        if (list == null)
            ctx.Fail("no list");
        else
            list.Add(item);
    }
}
