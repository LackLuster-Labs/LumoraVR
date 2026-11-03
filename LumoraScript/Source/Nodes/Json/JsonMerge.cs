// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// b's keys set on a in b's order, b's values winning. With deep, an object under a key both have is merged
// into the one already there, all the way down; anything else is replaced whole.
[Node("Data/JSON/Objects/Merge", Description = "B's keys set on a in b's order, b's values winning.")]
public sealed class JsonMerge : ReadNode
{
    public static ScriptJsonObject Run(ScriptContextBase ctx, ScriptJsonObject? a, ScriptJsonObject? b, bool deep = false)
    {
        if (a == null)
            return b ?? ScriptJsonObject.Empty;
        return b == null ? a : a.Merge(ctx.Collections, b, deep);
    }
}
