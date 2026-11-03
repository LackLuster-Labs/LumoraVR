// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Avatar;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Stands whoever sits in a seat up out of it. Says whether they stood: an empty seat, or for a Local script
// someone else in it, is a no, and no failure, since every machine runs the flow for its own person. A Shared
// script stands up anyone, a person's only its author, on the sitter's own machine, the way their own standing
// up does; released then says the ask went. No seat, or one that would not let them go, fails.
[Node("Interaction/Seats/Leave Seat", Capability.UsersAffect, Description = "Stands whoever sits in a seat up out of it. A Shared script reaches anyone, on their own machine.",
    Keywords = "stand up, unseat, exit vehicle, eject")]
public sealed class LeaveSeat : ActionNode
{
    [return: Port("released")]
    public static bool Run(ScriptContext ctx, Slot? seat)
    {
        if (ScriptUserAsks.OnHost(ctx) && ScriptReads.Visible(ctx, seat) && seat!.GetComponent<Seat>() is { IsDestroyed: false } taken
            && taken.OccupyingUser is { } sitter && !ReferenceEquals(sitter, ctx.World.LocalUser))
        {
            if (ScriptUserAsks.Away(ctx, sitter))
                return ScriptUserAsks.Send(ctx, sitter, new ScriptUserAsks.Request(ScriptUserAskKind.LeaveSeat) { Target = seat });
            ctx.Fail($"a person's script stands up only its author, not {ScriptReads.Named(sitter)}");
            return false;
        }
        var me = ctx.World.LocalUser;
        if (!ctx.IsMachineUser(me))
        {
            ctx.Fail(me is { IsDestroyed: false } ? $"a person's script stands up only its author, not {ScriptReads.Named(me)}"
                : "no one is at this machine");
            return false;
        }
        if (!ScriptReads.Reached(ctx, seat, "seat"))
            return false;
        if (seat.GetComponent<Seat>() is not { IsDestroyed: false } chair)
        {
            ctx.Fail($"{ScriptReads.Named(seat)} is not a seat");
            return false;
        }
        if (!chair.IsLocalUserSeated)
            return false;
        bool released = ScriptUserActions.Leave(ctx.World, me!, chair);
        if (!released)
            ctx.Fail($"{ScriptReads.Named(seat)} would not let them go");
        return released;
    }
}
