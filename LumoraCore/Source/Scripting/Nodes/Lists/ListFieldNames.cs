// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The names of the value fields Field Named reaches on a component, in the inspector's order, as a new list.
// Empty for a component the script cannot see.
[Node("World/Fields/Field Names", Capability.WorldRead, Description = "The names of the value fields Field Named reaches on a component, in the inspector's order, as a new list.")]
public sealed class ListFieldNames : ReadNode
{
    [return: Port("names")]
    public static ScriptList<string> Run(ScriptContext ctx, Component? component)
    {
        var names = new ScriptList<string>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, component))
            return names;
        for (int i = 0; i < component!.SyncMemberCount; i++)
        {
            if (ScriptComponents.IsValueField(component, i, out _, out _))
                names.Add(component.GetSyncMemberName(i));
        }
        return names;
    }
}
