// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A JSON array as a new list, each item read as the node's type the way Get reads a key. An item that does
// not read is the type's default in its place, and ok is then false. No array gives an empty list.
[Node("Data/JSON/Arrays/To List", Description = "A JSON array as a new list, each item read as the node's type the way Get reads a key.")]
public sealed class JsonToList<T> : ReadNode
{
    private static readonly ScriptType Type = ScriptTypes.Get(typeof(ScriptList<T>));

    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("list")]
    public static ScriptList<T> Run(ScriptContextBase ctx, ScriptJsonArray? array, out bool ok)
    {
        ok = ScriptJsonConvert.TryTo(ctx.Collections, array, Type, out var list);
        return list as ScriptList<T> ?? new ScriptList<T>(ctx.Collections);
    }
}
