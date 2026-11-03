// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The components on slot in their order, as a new list: every one the inspector lists, or with a type only those
// going by that name, as Find Component reads it. Later changes to the slot do not reach the list.
[Node("World/Components/Components", Capability.WorldRead, Description = "The components on slot in their order, as a new list: every one the inspector lists, or with a type only those going by that name, as Find Component reads it.",
    Keywords = "list components, all components")]
public sealed class ListComponents : ReadNode
{
    [return: Port("components")]
    public static ScriptList<Component> Run(ScriptContext ctx, Slot? slot, string? type = null)
    {
        var found = new ScriptList<Component>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, slot))
            return found;
        foreach (var component in slot!.Components)
        {
            if (ScriptComponents.Findable(ctx, component) && (string.IsNullOrEmpty(type) || ScriptComponents.IsNamed(component, type)))
                found.Add(component);
        }
        return found;
    }
}
