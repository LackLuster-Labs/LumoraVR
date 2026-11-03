// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The item at index, 0 first; the type's default when the index is outside the list.
[Node("Data/Lists/Get At", Description = "The item at index, 0 first; the type's default when the index is outside the list.",
    Keywords = "element, item at, index, array")]
public sealed class ListGetAt<T> : ReadNode
{
    [return: Port("item")]
    public static T? Run(ScriptList<T>? list, int index) => list == null ? default : list.Get(index);
}
