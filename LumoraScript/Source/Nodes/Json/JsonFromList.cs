// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A list as a JSON array, each item in its JSON form. Later changes to the list do not reach it. No list
// gives an empty array.
[Node("Data/JSON/Arrays/From List", Description = "A list as a JSON array, each item in its JSON form.")]
public sealed class JsonFromList<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("array")]
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptList<T>? list)
        => list == null ? ScriptJsonArray.Empty : (ScriptJsonArray)ScriptJsonConvert.From(ctx.Collections, list);
}
