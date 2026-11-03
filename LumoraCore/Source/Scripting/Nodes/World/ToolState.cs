// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether the tool on the slot is equipped, and if so whose, in which hand, the hand rig's slot, and
// whether it is being used this moment. How a hand uses its tool is only known where the person is, so
// in use arrives from their machine a moment late everywhere else. Nothing but false, nobody and None
// for a slot with no tool or a tool nobody has equipped.
[Node("Interaction/Tools/Tool State", Capability.UsersRead, Description = "Whether the tool on the slot is equipped, and if so whose, in which hand, the hand rig's slot, and whether it is being used this moment.")]
public sealed class ToolState : ReadNode
{
    [return: Port("equipped")]
    public static bool Run(ScriptContext ctx, Slot? slot, out User? user, out Chirality side, out Slot? hand, out bool inUse)
    {
        user = null;
        side = Chirality.None;
        hand = null;
        inUse = false;
        if (!ScriptReads.Visible(ctx, slot) || slot!.GetComponent<ToolItem>() is not { IsDestroyed: false } item
            || item.ActiveTool is not HandTool { IsDestroyed: false } holder)
            return false;
        var owner = holder.Slot.ActiveUser;
        if (!ScriptReads.Visible(ctx, owner) || !ScriptReads.Visible(ctx, holder))
            return false;
        user = owner;
        side = holder.Side.Value;
        hand = holder.Slot;
        inUse = holder.IsUnderLocalUser ? item.IsInUse : holder.ToolInUse.Value;
        return true;
    }
}
