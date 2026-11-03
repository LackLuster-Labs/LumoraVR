// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The slot of the seat a user sits in, found the way a seat finds its own occupant: their body hangs straight
// from the seat's space. Seated is whether they sit anywhere; no slot and false for someone standing.
[Node("Interaction/Seats/User Seat", Capability.UsersRead, Description = "The slot of the seat a user sits in, found the way a seat finds its own occupant: their body hangs straight from the seat's space.",
    Keywords = "is seated, is sitting, current seat, anchor of user")]
public sealed class UserSeat : ReadNode
{
    [return: Port("seat")]
    public static Slot? Run(ScriptContext ctx, User? user, out bool seated)
    {
        seated = false;
        if (!ScriptReads.Visible(ctx, user) || SeatIndex.Holding(user) is not { } seat)
            return null;
        seated = true;
        return ScriptReads.Visible(ctx, seat.Slot) ? seat.Slot : null;
    }
}
