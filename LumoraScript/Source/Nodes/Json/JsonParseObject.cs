// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Text read as a JSON object. ok is false, the object nothing and error why, when the text is not JSON or is
// JSON that is not an object.
[Node("Data/JSON/Parse Object", Description = "Text read as a JSON object. ok is false, the object nothing and error why, when the text is not JSON or is JSON that is not an object.")]
public sealed class JsonParseObject : ReadNode
{
    [return: Port("object")]
    public static ScriptJsonObject? Run(ScriptContextBase ctx, string? text, out bool ok, out string error)
    {
        ok = ScriptJsonReader.TryRead(ctx.Collections, text, out var json, out error) && json is ScriptJsonObject;
        if (!ok && error.Length == 0)
            error = $"the text is {ScriptJson.Describe(json)}, not an object";
        return ok ? (ScriptJsonObject)json! : null;
    }
}
