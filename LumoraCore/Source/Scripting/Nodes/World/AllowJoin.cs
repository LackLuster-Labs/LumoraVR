// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Script;

namespace Lumora.Core.Scripting;

// Lets the person in, unless another check turns them away. They are let in once the flow that said so has
// run on to its end, so a Set Join Role after it in the same flow still counts.
[Node("World/Session/Allow Join", Capability.Core, Description = "Lets the person in, unless another check turns them away.")]
public sealed class AllowJoin : ActionNode
{
    public static void Run(ScriptContext ctx, JoinRequest? request)
    {
        if (ScriptJoinChecks.Answerable(ctx, request) && !request!.Allow())
            ctx.Fail("the request was already answered");
    }
}
