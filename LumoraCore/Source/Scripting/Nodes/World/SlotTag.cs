// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Empty for a slot with no tag, and for one the script cannot see.
[Node("World/Slots/Properties/Tag", Capability.WorldRead, Description = "Reads the text tag a slot carries, empty when it has none.")]
public sealed class SlotTag : ReadNode
{
    [return: Port("tag")]
    public static string Run(ScriptContext ctx, Slot? slot)
        => ScriptReads.Visible(ctx, slot) ? slot!.Tag.Value ?? string.Empty : string.Empty;
}
