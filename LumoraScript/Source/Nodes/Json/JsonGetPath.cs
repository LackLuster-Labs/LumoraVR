// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The value a path leads to, such as users[0].name, read as the node's type the way Get reads a key. found
// is false when the path goes nowhere, is not a path, or its value does not read as that type. $ alone is
// the value itself; a negative index counts from the end.
[Node("Data/JSON/Paths/Get Path", Description = "Reads a typed value at a JSON path, such as users[0].name, and reports whether it was found.")]
public sealed class JsonGetPath<T> : ReadNode
{
    public static bool IsValidGenericType => ScriptJsonConvert.Carries(typeof(T));

    [return: Port("value")]
    public static T? Run(ScriptContextBase ctx, ScriptJson? json, string? path, out bool found)
    {
        found = false;
        if (json == null || !ScriptJsonPath.TryParse(path, out var parsed, out _) || !parsed.TryGet(json, out var member))
            return default;
        found = ScriptJsonConvert.TryTo<T>(ctx.Collections, member, out var value);
        return value;
    }
}
