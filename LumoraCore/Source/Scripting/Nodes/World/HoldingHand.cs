// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whose hand holds the slot's grabbable, its own or the nearest one above it, and which hand: nobody and
// None when it is not held. Something held by a grabber that is not a hand gives its holder and None.
[Node("Interaction/Grabbing/Holding Hand", Capability.UsersRead, Description = "Whose hand holds the slot's grabbable, its own or the nearest one above it, and which hand: nobody and None when it is not held.")]
public sealed class HoldingHand : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? slot, out Chirality side)
    {
        side = Chirality.None;
        if (!ScriptReads.Visible(ctx, slot) || ScriptHands.GrabbableAt(slot) is not { Grabber: { IsDestroyed: false } grabber } grabbable
            || !ScriptReads.Visible(ctx, grabbable as Component))
            return null;
        var user = grabber.OwningUser;
        if (!ScriptReads.Visible(ctx, user))
            return null;
        side = ScriptHands.HandOf(grabber)?.Side.Value ?? Chirality.None;
        return user;
    }
}
