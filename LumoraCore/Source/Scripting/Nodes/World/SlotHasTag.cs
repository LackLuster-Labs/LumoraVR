// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the slot's tag is exactly this text, case included. An empty tag asks whether it has none. False
// for a slot the script cannot see.
[Node("World/Slots/Properties/Has Tag", Capability.WorldRead, Description = "Whether the slot's tag is exactly this text, case included.")]
public sealed class SlotHasTag : ReadNode
{
    [return: Port("has")]
    public static bool Run(ScriptContext ctx, Slot? slot, string? tag)
        => ScriptReads.Visible(ctx, slot) && string.Equals(slot!.Tag.Value ?? string.Empty, tag ?? string.Empty, StringComparison.Ordinal);
}
