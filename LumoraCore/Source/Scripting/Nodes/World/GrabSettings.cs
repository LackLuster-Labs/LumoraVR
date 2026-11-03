// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// How the slot's grabbable is set up, from its own or the nearest one above it: whether there is one at all,
// the priority that wins when a hand reaches several, whether a held one may be resized, whether a drop
// target may take it when it is let go, whether someone may take it out of another person's hand, and
// whether only a hand reaching it, never a beam, may pick it up. All false and zero with no grabbable.
[Node("Interaction/Grabbing/Grab Settings", Capability.WorldRead, Description = "Reads an object's grab options, including priority, scaling, remote grabbing and whether it can be taken from another hand.")]
public sealed class GrabSettings : ReadNode
{
    [return: Port("grabbable")]
    public static bool Run(ScriptContext ctx, Slot? slot, out int priority, out bool scalable, out bool receivable, out bool stealable,
        out bool handOnly)
    {
        priority = 0;
        scalable = false;
        receivable = false;
        stealable = false;
        handOnly = false;
        if (!ScriptReads.Visible(ctx, slot) || ScriptHands.GrabbableAt(slot) is not { } grabbable
            || !ScriptReads.Visible(ctx, grabbable as Component))
            return false;
        priority = grabbable.GrabPriority;
        scalable = grabbable.Scalable;
        receivable = grabbable.Receivable;
        stealable = grabbable.CanBeStolen;
        handOnly = grabbable.AllowOnlyPhysicalGrab;
        return true;
    }
}
