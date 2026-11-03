// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// A JSON value read as the node's type, the way Get reads a key; ok is false, and the value the type's
// default, when it does not read as that type.
[Node("Data/JSON/Values/To Value", Description = "A JSON value read as the node's type, the way Get reads a key; ok is false, and the value the type's default, when it does not read as that type.")]
public sealed class JsonToValue<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("value")]
    public static T? Run(ScriptContextBase ctx, ScriptJson? json, out bool ok)
    {
        ok = ScriptJsonConvert.TryTo<T>(ctx.Collections, json, out var value);
        return value;
    }
}
