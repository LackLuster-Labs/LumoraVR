// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether an item equal to item is in the list.
[Node("Data/Lists/Contains", Description = "Whether an item equal to item is in the list.",
    Keywords = "has, includes, find")]
public sealed class ListContains<T> : ReadNode
{
    [return: Port("found")]
    public static bool Run(ScriptList<T>? list, T item) => list != null && list.Contains(item);
}
