// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The number the hierarchy sorts a slot among its siblings by, lowest first, siblings with the same number
// keeping the order of the parent's child list. It reaches every machine and saves with the world. Zero for
// a slot the script cannot see.
[Node("World/Slots/Properties/Order Offset", Capability.WorldRead, Description = "The number the hierarchy sorts a slot among its siblings by, lowest first, siblings with the same number keeping the order of the parent's child list.")]
public sealed class SlotOrderOffset : ReadNode
{
    [return: Port("offset")]
    public static long Run(ScriptContext ctx, Slot? slot) => ScriptReads.Visible(ctx, slot) ? slot!.OrderOffset.Value : 0L;
}
