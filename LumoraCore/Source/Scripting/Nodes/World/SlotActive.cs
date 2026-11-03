// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the slot and every parent above it are active.
[Node("World/Slots/Properties/Is Active", Capability.WorldRead, Description = "Whether the slot and every parent above it are active.",
    Keywords = "enabled, visible, shown")]
public sealed class SlotActive : ReadNode
{
    [return: Port("active")]
    public static bool Run(ScriptContext ctx, Slot? slot) => ScriptReads.Visible(ctx, slot) && slot!.IsActive;
}
