// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Script;

// Every key in the order they were first set, as a new list. No object gives an empty list.
[Node("Data/JSON/Objects/Keys", Description = "Every key in the order they were first set, as a new list.")]
public sealed class JsonKeys : ReadNode
{
    [return: Port("keys")]
    public static ScriptList<string> Run(ScriptContextBase ctx, ScriptJsonObject? @object)
    {
        var keys = new ScriptList<string>(ctx.Collections);
        if (@object != null)
        {
            foreach (var key in @object.Keys)
                keys.Add(key);
        }
        return keys;
    }
}
