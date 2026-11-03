// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Whether a person's hand could pick up the slot's grabbable at this moment, its own or the first one above it
// that would let that hand: grabbing allowed, not driven, not worn, and not already held unless it may be
// taken, and never out of that person's own other hand. A side of None asks about either hand. It says
// what the object allows; it does not reach for it.
[Node("Interaction/Grabbing/Can Grab", Capability.UsersRead, Description = "Checks whether the specified user's hand is currently allowed to grab the object.")]
public sealed class HandCanGrab : ReadNode
{
    [return: Port("can")]
    public static bool Run(ScriptContext ctx, Slot? slot, User? user, Chirality side = Chirality.Right)
    {
        if (!ScriptReads.Visible(ctx, slot))
            return false;
        if (side == Chirality.None)
            return Could(ctx, slot!, user, Chirality.Left) || Could(ctx, slot!, user, Chirality.Right);
        return Could(ctx, slot!, user, side);
    }

    private static bool Could(ScriptContext ctx, Slot slot, User? user, Chirality side)
    {
        var grabber = ScriptHands.GrabberOf(ScriptHands.Hand(ctx, user, side));
        if (grabber == null)
            return false;
        for (var current = slot; current != null && !current.IsRemoved; current = current.Parent)
        {
            if (!ReferenceEquals(current, slot) && current.GetComponent<SearchBlock>() != null)
                return false;
            foreach (var grabbable in current.GetComponentsImplementing<IGrabbable>())
            {
                if (grabbable is Component { IsDestroyed: false } component && ScriptReads.Visible(ctx, component) && grabbable.CanGrab(grabber))
                    return true;
            }
        }
        return false;
    }
}
