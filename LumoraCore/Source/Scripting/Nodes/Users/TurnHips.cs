// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Turns a user so their hips face a rotation, the whole body turning about where it stands. The hips face where
// Users/Body Pose says they do. A Local script turns only the user at its machine. A Shared script turns anyone, a
// person's only its author, on that user's own machine. An unset rotation faces straight ahead; one that is not a
// finite number fails and turns nobody.
[Node("Users/Movement/Turn Hips", Capability.UsersAffect, Description = "Turns a user so their hips face a rotation, the whole body turning about where it stands. A Shared script reaches anyone, on their own machine.",
    Keywords = "rotate user, set facing, turn player, face direction")]
public sealed class TurnHips : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, [Default(0, 0, 0, 1)] floatQ rotation)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if (!away && !ScriptReads.HereFor(ctx, user))
            return;
        if (user!.Root is not { IsDestroyed: false } root)
        {
            ctx.Fail("the user has no body here");
            return;
        }
        if (!float.IsFinite(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w))
        {
            ctx.Fail("the rotation is not a finite number");
            return;
        }
        if (away)
            ScriptUserAsks.Send(ctx, user, new ScriptUserAsks.Request(ScriptUserAskKind.TurnHips) { Rotation = rotation });
        else
            ScriptUserActions.TurnHips(ctx.World, user, root, rotation);
    }
}
