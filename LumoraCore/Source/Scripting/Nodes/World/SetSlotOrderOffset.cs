// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Judged by the gate like the author's own edit. Never the world root, a protected slot, or anything in
// someone's body or hands.
[Node("World/Slots/Properties/Set Order Offset", Capability.WorldWrite, Description = "Changes a slot's sorting offset among its siblings.")]
public sealed class SetSlotOrderOffset : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? slot, long offset)
    {
        if (ScriptReads.Reached(ctx, slot))
            ScriptSlotWrites.Write(ctx, slot!, slot!.OrderOffset, offset);
    }
}
