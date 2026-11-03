// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;

namespace Lumora.Script;

// The first value a JSONPath query finds, as Select would list it; found is false when it finds nothing or
// is not a query.
[Node("Data/JSON/Paths/Select One", Description = "The first value a JSONPath query finds, as Select would list it; found is false when it finds nothing or is not a query.")]
public sealed class JsonSelectOne : ValueNode
{
    [return: Port("match")]
    public static ScriptJson? Run(ScriptJson? json, string? query, out bool found)
    {
        found = false;
        if (json == null || !ScriptJsonQuery.TryParse(query, out var parsed, out _))
            return null;
        var matches = new List<ScriptJson>();
        parsed.Run(json, matches);
        found = matches.Count > 0;
        return found ? matches[0] : null;
    }
}
