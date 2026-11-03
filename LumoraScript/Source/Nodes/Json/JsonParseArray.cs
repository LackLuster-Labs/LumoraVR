// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Text read as a JSON array. ok is false, the array nothing and error why, when the text is not JSON or is
// JSON that is not an array.
[Node("Data/JSON/Parse Array", Description = "Text read as a JSON array. ok is false, the array nothing and error why, when the text is not JSON or is JSON that is not an array.")]
public sealed class JsonParseArray : ReadNode
{
    [return: Port("array")]
    public static ScriptJsonArray? Run(ScriptContextBase ctx, string? text, out bool ok, out string error)
    {
        ok = ScriptJsonReader.TryRead(ctx.Collections, text, out var json, out error) && json is ScriptJsonArray;
        if (!ok && error.Length == 0)
            error = $"the text is {ScriptJson.Describe(json)}, not an array";
        return ok ? (ScriptJsonArray)json! : null;
    }
}
