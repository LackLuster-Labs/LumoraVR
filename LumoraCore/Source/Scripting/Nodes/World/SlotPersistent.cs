// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Saved: whether the world keeps the slot when it saves, which takes its own persistent flag and every
// parent's, and never holds for a slot that exists on one machine only. Self: its own flag alone. Both
// false for a slot the script cannot see.
[Node("World/Slots/Properties/Persistent", Capability.WorldRead, Description = "Saved: whether the world keeps the slot when it saves, which takes its own persistent flag and every parent's, and never holds for a slot that exists on one machine only.")]
public sealed class SlotPersistent : ReadNode
{
    [return: Port("saved")]
    public static bool Run(ScriptContext ctx, Slot? slot, out bool self)
    {
        self = false;
        if (!ScriptReads.Visible(ctx, slot))
            return false;
        self = slot!.Persistent.Value;
        for (var current = slot; current != null; current = current.Parent)
        {
            if (!current.Persistent.Value || current.IsLocalElement)
                return false;
        }
        return true;
    }
}
