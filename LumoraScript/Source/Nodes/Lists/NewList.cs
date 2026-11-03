// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// An empty list. It is in the flow, so every node after it that reads its list reads this one list.
[Node("Data/Lists/New List", Description = "An empty list.",
    Keywords = "create list, array, empty list")]
public sealed class NewList<T> : ActionNode
{
    [return: Port("list")]
    public static ScriptList<T> Run(ScriptContextBase ctx) => new(ctx.Collections);
}
