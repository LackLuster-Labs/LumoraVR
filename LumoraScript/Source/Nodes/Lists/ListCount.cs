// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// How many items a list holds; 0 for no list.
[Node("Data/Lists/Count", Description = "How many items a list holds; 0 for no list.",
    Keywords = "length, size, array")]
public sealed class ListCount<T> : ReadNode
{
    public static int Run(ScriptList<T>? list) => list?.Count ?? 0;
}
