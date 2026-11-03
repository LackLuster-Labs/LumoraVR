// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Text read as JSON: an object, an array or a single value. ok is false, the value nothing and error what is
// wrong and where, when the text is not JSON. Comments and a comma before a closing bracket are let
// through.
[Node("Data/JSON/Parse", Description = "Text read as JSON: an object, an array or a single value. ok is false, the value nothing and error what is wrong and where, when the text is not JSON.",
    Keywords = "deserialize, decode, read json, from text")]
public sealed class JsonParse : ReadNode
{
    [return: Port("json")]
    public static ScriptJson? Run(ScriptContextBase ctx, string? text, out bool ok, out string error)
    {
        ok = ScriptJsonReader.TryRead(ctx.Collections, text, out var json, out error);
        return json;
    }
}
