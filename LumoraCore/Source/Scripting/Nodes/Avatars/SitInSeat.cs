// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sits a user in a seat, the way touching it would. Says whether they sat: a seat that is taken, one they may not
// use, or anyone else is a no. Each of those fails as well, except anyone else for a Local script, whose copy on
// that person's machine sits them. A Shared script sits anyone, a person's only its author, on that user's own
// machine, which sits them through the seat's own rules; sat then says the seat would take them and the ask went.
[Node("Interaction/Seats/Sit In Seat", Capability.UsersAffect, Description = "Sits a user in a seat, the way touching it would. A Shared script reaches anyone, on their own machine.",
    Keywords = "sit, seat user, enter vehicle, ride")]
public sealed class SitInSeat : ActionNode
{
    [return: Port("sat")]
    public static bool Run(ScriptContext ctx, Slot? seat, User? user)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if ((!away && !ScriptReads.HereFor(ctx, user)) || !ScriptReads.Reached(ctx, seat, "seat"))
            return false;
        if (seat.GetComponent<Seat>() is not { IsDestroyed: false } chair)
        {
            ctx.Fail($"{ScriptReads.Named(seat)} is not a seat");
            return false;
        }
        bool sat = away ? chair.CanSit(user!) && ScriptUserAsks.Send(ctx, user!, new ScriptUserAsks.Request(ScriptUserAskKind.SitInSeat) { Target = seat })
            : ScriptUserActions.Sit(ctx.World, user!, chair);
        if (!sat)
            ctx.Fail($"{ScriptReads.Named(seat)} would not take them");
        return sat;
    }
}
