// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Script;

// Every value a JSONPath query finds, in document order, as an array: $.store.book[*].author,
// $..price, $.items[?(@.count > 2 && @.tag == 'red')], $.list[-2:] and the rest. ok is false, and error
// why, when the query is not one.
[Node("Data/JSON/Paths/Select", Description = "Finds every value matching a JSONPath query and returns them as an array.",
    Keywords = "jsonpath, query, filter, search, find all")]
public sealed class JsonSelect : ReadNode
{
    [return: Port("matches")]
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJson? json, string? query, out bool ok, out string error)
    {
        ok = ScriptJsonQuery.TryParse(query, out var parsed, out error);
        if (!ok || json == null)
            return ScriptJsonArray.Empty;
        var found = new List<ScriptJson>();
        parsed.Run(json, found);
        return ScriptJsonArray.Of(ctx.Collections, found);
    }
}
