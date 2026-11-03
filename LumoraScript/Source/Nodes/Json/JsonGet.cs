// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The value under key, read as the node's type. found is false, and the value the type's default, when the
// object has no such key or its value does not read as that type. Numbers read across number types, whole
// ones only into whole types; text reads as the type's text form; an array of numbers reads as a vector;
// and anything reads as text.
[Node("Data/JSON/Objects/Get", Description = "The value under key, read as the node's type. found is false, and the value the type's default, when the object has no such key or its value does not read as that type.",
    Keywords = "read key, property, field, member, value of key")]
public sealed class JsonGet<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("value")]
    public static T? Run(ScriptContextBase ctx, ScriptJsonObject? @object, string? key, out bool found)
    {
        found = false;
        if (@object == null || key == null || !@object.TryGet(key, out var member))
            return default;
        found = ScriptJsonConvert.TryTo<T>(ctx.Collections, member, out var value);
        return value;
    }
}
