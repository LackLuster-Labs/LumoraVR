// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a user to the locomotion whose name is the one given, or, unless exact, the first whose name holds it.
// Says whether it switched: no such locomotion, one the world does not allow, or anyone else is a no. Each of
// those fails as well, except anyone else for a Local script, whose copy on that person's machine switches them.
// A Shared script switches anyone, a person's only its author, on that user's own machine, which picks the
// locomotion and judges it; switched then says the ask went.
[Node("Users/Movement/Switch Locomotion", Capability.UsersAffect, Description = "Moves a user to the locomotion whose name is the one given, or, unless exact, the first whose name holds it. A Shared script reaches anyone, on their own machine.",
    Keywords = "set locomotion, change movement mode, enable flying")]
public sealed class SwitchLocomotion : ActionNode
{
    [return: Port("switched")]
    public static bool Run(ScriptContext ctx, User? user, string name, bool exact = false)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if (!away && !ScriptReads.HereFor(ctx, user))
            return false;
        if (string.IsNullOrEmpty(name))
        {
            ctx.Fail("no locomotion name");
            return false;
        }
        if (away)
            return ScriptUserAsks.Send(ctx, user!, new ScriptUserAsks.Request(ScriptUserAskKind.SwitchLocomotion) { Text = name, Flag = exact });
        if (user!.Root?.Slot is not { IsDestroyed: false } body || body.GetComponent<LocomotionController>() is not { IsDestroyed: false } controller)
        {
            ctx.Fail("the user has no locomotion here");
            return false;
        }
        if (ScriptUserActions.Switch(ctx.World, user, controller, name, exact, out var match))
            return true;
        ctx.Fail(match == null ? $"no locomotion named '{name}'" : $"'{match.DisplayName}' is not allowed here");
        return false;
    }
}
