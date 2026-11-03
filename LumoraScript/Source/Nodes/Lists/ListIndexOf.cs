// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Where the first item equal to item sits, 0 first; -1 when no item is, or there is no list.
[Node("Data/Lists/Index Of", Description = "Where the first item equal to item sits, 0 first; -1 when no item is, or there is no list.",
    Keywords = "find, search, position")]
public sealed class ListIndexOf<T> : ReadNode
{
    [return: Port("index")]
    public static int Run(ScriptList<T>? list, T item) => list?.IndexOf(item) ?? -1;
}
