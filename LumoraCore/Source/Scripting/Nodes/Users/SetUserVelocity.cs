// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Sets how fast a user moves, in metres a second through the world. Their body carries on from it, so gravity,
// the ground and their own walking act on it from there: upward lifts them off the floor, and along the ground
// their footing brakes it as it brakes a walk. While they fly or sit nothing moves them. A Local script sets only
// the user at its machine. A Shared script sets anyone, a person's only its author, on that user's own machine,
// at most ScriptUserActions.MaxSpeed. A user with no body that walks, and a velocity that is not a finite number,
// fail.
[Node("Users/Movement/Set Velocity", Capability.UsersAffect, Description = "Sets how fast a user moves, in metres a second through the world. A Shared script reaches anyone, on their own machine.",
    Keywords = "fling, launch, stop user, character velocity, user velocity")]
public sealed class SetUserVelocity : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, float3 velocity)
    {
        if (ScriptUserAsks.Away(ctx, user))
        {
            if (ScriptMotion.Finite(ctx, velocity))
                ScriptUserAsks.Send(ctx, user, new ScriptUserAsks.Request(ScriptUserAskKind.SetVelocity) { Vector = velocity });
            return;
        }
        if (ScriptMotion.OwnCharacter(ctx, user, velocity) is { } character)
            ScriptUserActions.SetVelocity(character, velocity, ScriptUserAsks.OnHost(ctx));
    }
}
