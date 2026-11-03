// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Turns the person away, with the reason they are shown; nothing another check says lets them in after.
[Node("World/Session/Refuse Join", Capability.Core, Description = "Turns the person away, with the reason they are shown; nothing another check says lets them in after.")]
public sealed class RefuseJoin : ActionNode
{
    public static void Run(ScriptContext ctx, JoinRequest? request, string? reason)
    {
        if (ScriptJoinChecks.Answerable(ctx, request) && !request!.Refuse(reason))
            ctx.Fail("the request was already answered");
    }
}
