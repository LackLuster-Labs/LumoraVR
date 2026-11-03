// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot's own active flag, whatever its parents' are; Is Active reads the whole chain. False for a slot
// the script cannot see.
[Node("World/Slots/Properties/Active Self", Capability.WorldRead, Description = "The slot's own active flag, whatever its parents' are; Is Active reads the whole chain.")]
public sealed class SlotActiveSelf : ReadNode
{
    [return: Port("active")]
    public static bool Run(ScriptContext ctx, Slot? slot) => ScriptReads.Visible(ctx, slot) && slot!.ActiveSelf.Value;
}
