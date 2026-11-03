// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// The object without the value a path leads to; removed is false, and the object as it was, when the path
// goes nowhere or is not a path.
[Node("Data/JSON/Paths/Remove Path", Description = "The object without the value a path leads to; removed is false, and the object as it was, when the path goes nowhere or is not a path.")]
public sealed class JsonRemovePath : ReadNode
{
    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptJsonObject? @object, string? path, out bool removed)
    {
        var start = @object ?? ScriptJsonObject.Empty;
        removed = ScriptJsonPath.TryParse(path, out var parsed, out _) && parsed.TryRemove(ctx.Collections, start, out start);
        return start;
    }
}
