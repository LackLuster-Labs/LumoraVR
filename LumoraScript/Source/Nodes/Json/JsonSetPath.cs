// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The object with the value a path leads to set, making what is missing on the way: an object for a name,
// an array for index 0, and an index one past the end appends. ok is false, and the object as it was, when
// the path is not one or would have to go through a value of the wrong kind. No object starts from an
// empty one.
[Node("Data/JSON/Paths/Set Path", Description = "Returns JSON with a value changed at the given path, creating missing objects or array entries where possible.")]
public sealed class JsonSetPath<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptJsonObject? @object, string? path, T value, out bool ok)
    {
        var start = @object ?? ScriptJsonObject.Empty;
        ok = false;
        if (!ScriptJsonPath.TryParse(path, out var parsed, out _) || parsed.Steps.Count == 0)
            return start;
        ok = parsed.TrySet(ctx.Collections, start, ScriptJsonConvert.From(ctx.Collections, value), out var result);
        return result;
    }
}
