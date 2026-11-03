// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Input;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// One hand, or both with a side of None, lets go of whatever it holds, as opening the grip does: thrown with the
// hand's motion and offered to a drop surface. Whose hand is the person who set the call off (a grab, a press, a
// touch), else the user at this machine. A Local script's copy acts only on the machine of whoever that is; a
// Shared script reaches them on their own machine, a person's only its author. Released says a hand was there to
// do it, holding anything or not, or for someone at another machine that the ask went, and the call fails when
// none was. -xlinka
[Node("Interaction/Grabbing/Let Go", Capability.UsersAffect, Description = "One hand of whoever set the call off, else of the user at this machine, or both with a side of None, lets go of whatever it holds, as opening the grip does. A Shared script reaches them on their own machine.",
    Keywords = "drop, release, unhold, force drop")]
public sealed class LetGo : ActionNode
{
    [return: Port("released")]
    public static bool Run(ScriptContext ctx, Chirality side = Chirality.Right)
    {
        if (!ctx.CausedHere)
        {
            var cause = ctx.Cause!;
            if (ScriptUserAsks.Away(ctx, cause))
                return ScriptUserAsks.Send(ctx, cause, new ScriptUserAsks.Request(ScriptUserAskKind.LetGo) { Side = side });
            if (!ctx.IsLocal)
                ctx.Fail(!ScriptReads.Visible(ctx, cause) ? "whoever set it off has left"
                    : ctx.MayActOn(cause) ? $"{ScriptReads.Named(cause)} is not at this machine"
                    : $"a person's script lets go only for its author, not {ScriptReads.Named(cause)}");
            return false;
        }
        var user = ctx.World.LocalUser;
        if (!ctx.IsMachineUser(user) || user!.Root is not { IsDestroyed: false } root)
        {
            ctx.Fail("no one is at this machine");
            return false;
        }
        if (!ScriptUserActions.LetGo(ctx.World, user, root, side))
        {
            ctx.Fail("no hand to let go with");
            return false;
        }
        return true;
    }
}
