// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Linq;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The methods Call Method may call on a component, by name in order, as a new list. Empty for a component the
// script cannot see, or one whose type opens none.
[Node("World/Components/Method Names", Capability.WorldRead, Description = "The methods Call Method may call on a component, by name in order, as a new list.")]
public sealed class ListMethodNames : ReadNode
{
    [return: Port("names")]
    public static ScriptList<string> Run(ScriptContext ctx, Component? component)
    {
        var names = new ScriptList<string>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, component))
            return names;
        foreach (var name in ScriptComponents.MethodNames(component!).OrderBy(n => n, StringComparer.Ordinal))
            names.Add(name);
        return names;
    }
}
