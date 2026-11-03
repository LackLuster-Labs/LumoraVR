// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Adds to how fast a user moves, in metres a second through the world, the way Set Velocity sets it: a jump pad,
// a dash, a knock back. velocity is the change of speed; people have no weight here, so the same push moves
// anyone the same. A Local script pushes only the user at its machine. A Shared script pushes anyone, a person's
// only its author, on that user's own machine, never past ScriptUserActions.MaxSpeed. A user with no body that
// walks, and a velocity that is not a finite number, fail.
[Node("Users/Movement/Push", Capability.UsersAffect, Description = "Adds a velocity change to a user's movement, for effects such as jump pads or knockback. A Shared script reaches anyone, on their own machine.",
    Keywords = "add velocity, apply force, launch, knockback, jump pad, boost")]
public sealed class PushUser : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, [Default(0, 5, 0)] float3 velocity)
    {
        if (ScriptUserAsks.Away(ctx, user))
        {
            if (ScriptMotion.Finite(ctx, velocity))
                ScriptUserAsks.Send(ctx, user, new ScriptUserAsks.Request(ScriptUserAskKind.Push) { Vector = velocity });
            return;
        }
        if (ScriptMotion.OwnCharacter(ctx, user, velocity) is { } character)
            ScriptUserActions.Push(character, velocity, ScriptUserAsks.OnHost(ctx));
    }
}
