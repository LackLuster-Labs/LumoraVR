// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// a's items, then b's, as one array.
[Node("Data/JSON/Arrays/Concat", Description = "A's items, then b's, as one array.")]
public sealed class JsonConcat : ReadNode
{
    public static ScriptJsonArray Run(ScriptContextBase ctx, ScriptJsonArray? a, ScriptJsonArray? b)
    {
        if (a == null || a.Count == 0)
            return b ?? ScriptJsonArray.Empty;
        return b == null || b.Count == 0 ? a : a.Concat(ctx.Collections, b);
    }
}
