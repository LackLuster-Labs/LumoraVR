// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Where the first item equal to value is, 0 first, or -1. Equal is the same JSON: numbers by value,
// objects whatever the order of their keys.
[Node("Data/JSON/Arrays/Index Of", Description = "Where the first item equal to value is, 0 first, or -1.")]
public sealed class JsonIndexOf<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("index")]
    public static int Run(ScriptContextBase ctx, ScriptJsonArray? array, T value)
        => array == null || array.Count == 0 ? -1 : array.IndexOf(ScriptJsonConvert.From(ctx.Collections, value));
}
