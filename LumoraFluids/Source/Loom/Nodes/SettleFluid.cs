// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Lets a system come to rest at once: every machine relaxes it and the keeper writes its whole ledger again. Only a
// script running on the host can; anywhere else it fails.
[Node("Physics/Fluids/Settle Fluid", Capability.Physics, Description = "Requests that a fluid system come to rest. This action must run on the host.")]
public sealed class SettleFluid : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? system)
    {
        var target = FluidScriptReach.System(ctx, system, fail: true);
        if (target == null)
            return;
        if (!FluidScriptReach.OnHost(ctx))
        {
            ctx.Fail("only the host settles fluid");
            return;
        }
        if (!FluidScriptReach.Command(target, FluidCommandKind.Settle, float3.Zero, 0f, 0f))
            ctx.Fail("the settle could not be written down");
    }
}
