// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// slot's children in order, as a new list; a child the script may not see is left out, and a slot it may
// not see has none. Later changes to the slot do not reach the list.
[Node("World/Slots/Hierarchy/Children", Capability.WorldRead, Description = "Slot's children in order, as a new list; a child the script may not see is left out, and a slot it may not see has none.",
    Keywords = "child list, get children, all children, list children")]
public sealed class ListChildren : ReadNode
{
    [return: Port("children")]
    public static ScriptList<Slot> Run(ScriptContext ctx, Slot? slot)
    {
        var children = new ScriptList<Slot>(ctx.Collections);
        if (!ScriptReads.Visible(ctx, slot))
            return children;
        foreach (var child in slot!.Children)
        {
            if (ScriptReads.Visible(ctx, child))
                children.Add(child);
        }
        return children;
    }
}
