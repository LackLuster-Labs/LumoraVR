// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("World/Slots/Hierarchy/Parent", Capability.WorldRead, Description = "Returns the slot directly above this slot in the hierarchy.",
    Keywords = "get parent, parent slot, up one level")]
[Evaluates(Evaluation.Tree)]
public sealed class ParentSlot : ReadNode
{
    [return: Port("parent")]
    public static Slot? Run(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return null;
        var parent = slot!.Parent;
        return ScriptReads.Visible(ctx, parent) ? parent : null;
    }
}
