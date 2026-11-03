// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

[Node("World/Slots/Properties/Set Active", Capability.WorldWrite, Description = "Enables or disables a slot and its contents.",
    Keywords = "enable, disable, show, hide, toggle, visible")]
public sealed class SetSlotActive : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, bool active)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptWrites.Set(ctx, slot!.ActiveSelf, active);
    }
}
