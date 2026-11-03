// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether slot is anywhere under parent, at any depth. Include self counts the slot itself as under
// itself. False when the script cannot see either.
[Node("World/Slots/Hierarchy/Is Child Of", Capability.WorldRead, Description = "Whether slot is anywhere under parent, at any depth.",
    Keywords = "descendant, under, inside hierarchy, contains")]
public sealed class SlotIsChildOf : ReadNode
{
    [return: Port("under")]
    public static bool Run(ScriptContext ctx, Slot? slot, Slot? parent, bool includeSelf = false)
    {
        if (!ScriptReads.Visible(ctx, slot) || !ScriptReads.Visible(ctx, parent))
            return false;
        return ReferenceEquals(slot, parent) ? includeSelf : slot!.IsDescendantOf(parent!);
    }
}
