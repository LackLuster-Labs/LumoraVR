// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Eases a user to a scale over some seconds, feet held where they stand, inside the scale range and what their
// role allows. A Local script scales only the user at its machine. A Shared script scales anyone, a person's only
// its author, on that user's own machine, which holds it to their limits. A scale or time that is not a finite
// number fails.
[Node("Users/Movement/Scale User", Capability.UsersAffect, Description = "Eases a user to a scale over some seconds, feet held where they stand, inside what their role allows. A Shared script reaches anyone, on their own machine.",
    Keywords = "resize user, size, grow, shrink")]
public sealed class ScaleUser : ActionNode
{
    public static void Run(ScriptContext ctx, User? user, float scale = 1f, float seconds = 0f)
    {
        bool away = ScriptUserAsks.Away(ctx, user);
        if (!away && !ScriptReads.HereFor(ctx, user))
            return;
        if (user!.Root is not { IsDestroyed: false } root)
        {
            ctx.Fail("the user has no body here");
            return;
        }
        if (!float.IsFinite(scale) || !float.IsFinite(seconds))
        {
            ctx.Fail("scale and seconds have to be finite numbers");
            return;
        }
        if (away)
            ScriptUserAsks.Send(ctx, user, new ScriptUserAsks.Request(ScriptUserAskKind.Scale) { Amount = scale, Seconds = seconds });
        else
            ScriptUserActions.Scale(ctx.World, user, root, scale, seconds);
    }
}
