// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The object with key set to value: a key it has keeps its place, a new one goes on the end. The object
// given is not changed. No object starts from an empty one, and no key gives the object back as it was.
// A list or dictionary goes in as it is now, as an array or an object.
[Node("Data/JSON/Objects/Set", Description = "The object with key set to value: a key it has keeps its place, a new one goes on the end.",
    Keywords = "write key, property, field, put")]
public sealed class JsonSet<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptJsonObject? @object, string? key, T value)
    {
        var start = @object ?? ScriptJsonObject.Empty;
        return key == null ? start : start.Set(ctx.Collections, key, ScriptJsonConvert.From(ctx.Collections, value));
    }
}
