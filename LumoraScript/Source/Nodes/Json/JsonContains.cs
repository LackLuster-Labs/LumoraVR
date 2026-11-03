// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Whether the array has an item equal to value, the way Index Of finds one.
[Node("Data/JSON/Arrays/Contains", Description = "Whether the array has an item equal to value, the way Index Of finds one.")]
public sealed class JsonContains<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("contains")]
    public static bool Run(ScriptContextBase ctx, ScriptJsonArray? array, T value)
        => array != null && array.Count > 0 && array.IndexOf(ScriptJsonConvert.From(ctx.Collections, value)) >= 0;
}
