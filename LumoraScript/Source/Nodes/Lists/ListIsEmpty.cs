// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// True for a list with no items, and for no list.
[Node("Data/Lists/Is Empty", Description = "True for a list with no items, and for no list.",
    Keywords = "has items, zero length")]
public sealed class ListIsEmpty<T> : ReadNode
{
    [return: Port("empty")]
    public static bool Run(ScriptList<T>? list) => list == null || list.Count == 0;
}
