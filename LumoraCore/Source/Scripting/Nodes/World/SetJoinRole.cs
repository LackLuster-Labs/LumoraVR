// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// The role the person arrives with, by its name: one the world's mode offers, never Host. It is pinned to
// them like a role the host picks on the session screen, so it saves with the world.
[Node("World/Session/Set Join Role", Capability.Core, Description = "The role the person arrives with, by its name: one the world's mode offers, never Host.")]
public sealed class SetJoinRole : ActionNode
{
    public static void Run(ScriptContext ctx, JoinRequest? request, string? role)
    {
        if (!ScriptJoinChecks.Answerable(ctx, request))
            return;
        if (ScriptJoinChecks.AssignableRole(ctx.World, role, out string why) is not { } name)
        {
            ctx.Fail(why);
            return;
        }
        if (!request!.ChooseRole(name))
            ctx.Fail("the request was already answered");
    }
}
