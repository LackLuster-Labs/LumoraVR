// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Who is holding the slot's grabbable, its own or the nearest one above it, as Holding Hand finds it, or
// nobody.
[Node("Interaction/Grabbing/Held By", Capability.UsersRead, Description = "Who is holding the slot's grabbable, its own or the nearest one above it, or nobody.",
    Keywords = "grabber, who holds, holder, held user")]
public sealed class SlotHolder : ReadNode
{
    [return: Port("user")]
    public static User? Run(ScriptContext ctx, Slot? slot)
    {
        if (!ScriptReads.Visible(ctx, slot) || ScriptHands.GrabbableAt(slot) is not { } grabbable
            || !ScriptReads.Visible(ctx, grabbable as Component))
            return null;
        var user = grabbable.Grabber is { IsDestroyed: false } grabber ? grabber.OwningUser : null;
        return ScriptReads.Visible(ctx, user) ? user : null;
    }
}
