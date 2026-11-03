// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The array with value on the end. The array given is not changed; no array starts from an empty one.
[Node("Data/JSON/Arrays/Append", Description = "The array with value on the end.")]
public sealed class JsonAppend<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? array, T value)
        => (array ?? ScriptJsonArray.Empty).Add(ctx.Collections, ScriptJsonConvert.From(ctx.Collections, value));
}
