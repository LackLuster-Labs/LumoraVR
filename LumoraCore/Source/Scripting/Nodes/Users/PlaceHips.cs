// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Moves a user so their hips are at a point, carrying the whole body along. The hips are where Users/Body Pose
// says they are. A Local script moves only the user at its machine. A Shared script moves anyone, a person's only
// its author, on that user's own machine. A point that is not a finite number fails.
[Node("Users/Movement/Place Hips", Capability.UsersAffect, Description = "Moves a user so their hips are at a point, carrying the whole body along. A Shared script reaches anyone, on their own machine.",
    Keywords = "teleport, move user, set user position, warp, respawn")]
public sealed class PlaceHips : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, float3 position)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if (!away && !ScriptReads.HereFor(ctx, user))
            return;
        if (user!.Root is not { IsDestroyed: false } root)
        {
            ctx.Fail("the user has no body here");
            return;
        }
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
        {
            ctx.Fail("the position is not a finite number");
            return;
        }
        if (away)
            ScriptUserAsks.Send(ctx, user, new ScriptUserAsks.Request(ScriptUserAskKind.PlaceHips) { Vector = position });
        else
            ScriptUserActions.PlaceHips(ctx.World, user, root, position);
    }
}
