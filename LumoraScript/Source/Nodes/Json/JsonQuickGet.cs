// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// One key read straight from JSON text: Parse Object and Get in one. found is false when the text is not a
// JSON object, has no such key, or the key's value does not read as the node's type.
[Node("Data/JSON/Quick Get", Description = "One key read straight from JSON text: Parse Object and Get in one. found is false when the text is not a JSON object, has no such key, or the key's value does not read as the node's type.")]
public sealed class JsonQuickGet<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("value")]
    public static T? Run(ScriptContextBase ctx, string? text, string? key, out bool found)
    {
        found = false;
        if (key == null || !ScriptJsonReader.TryRead(ctx.Collections, text, out var json, out _) || json is not ScriptJsonObject obj
            || !obj.TryGet(key, out var member))
            return default;
        found = ScriptJsonConvert.TryTo<T>(ctx.Collections, member, out var value);
        return value;
    }
}
