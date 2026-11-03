// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Gives a user a set of locomotions: a copy of the slot given, with every locomotion in it, goes under their own,
// in their picker by its slot's name. Those installed before go first when clear is set. Says whether anything
// came in, and fails when nothing did. A Local script gives only the user at its machine. A Shared script gives
// anyone, a person's only its author, on that user's own machine, which makes the copy; installed then says the
// ask went.
[Node("Users/Movement/Install Locomotions", Capability.UsersAffect, Description = "Gives a user a set of locomotions: a copy of the slot given, with every locomotion in it, goes under their own, in their picker by its slot's name. A Shared script reaches anyone, on their own machine.")]
public sealed class InstallLocomotions : ActionNode
{
    [return: Port("installed")]
    public static bool Run(ScriptContext ctx, Slot? from, User? user, bool clear = false)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if ((!away && !ScriptReads.HereFor(ctx, user)) || !ScriptReads.Reached(ctx, from, "source"))
            return false;
        if (away)
            return ScriptUserAsks.Send(ctx, user!, new ScriptUserAsks.Request(ScriptUserAskKind.InstallLocomotions) { Target = from, Flag = clear });
        if (user!.Root?.Slot?.GetComponent<LocomotionController>() is not { IsDestroyed: false } controller)
        {
            ctx.Fail("the user has no locomotion here");
            return false;
        }
        if (ScriptUserActions.Install(ctx.World, user, controller, from, clear) > 0)
            return true;
        ctx.Fail($"{ScriptReads.Named(from)} holds no locomotion to install");
        return false;
    }
}
