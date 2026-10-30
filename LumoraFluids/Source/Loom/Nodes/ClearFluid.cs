// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Empties a system, or takes out only what one person poured into it when origin is given. On the host it happens
// at once. Anywhere else it is asked for: your own fluid always goes, the rest only for someone who may edit the
// system or a moderator. A Local script cannot clear fluid for everyone, so it fails.
[Node("Physics/Fluids/Clear Fluid", Capability.Physics, Description = "Removes all fluid from a system, or only the fluid poured by a specified user.")]
public sealed class ClearFluid : ActionNode
{
    public static void Run(ScriptContext ctx, Slot? system, User? origin = null)
    {
        var target = FluidScriptReach.System(ctx, system, fail: true);
        if (target == null)
            return;
        if (origin != null && !FluidScriptReach.Reached(ctx, origin, "origin"))
            return;
        if (ctx.IsLocal)
        {
            ctx.Fail("a Local script cannot clear fluid for everyone");
            return;
        }
        if (FluidScriptReach.OnHost(ctx))
        {
            if (origin == null)
            {
                if (!FluidScriptReach.Command(target, FluidCommandKind.ClearAll, float3.Zero, 0f, 0f))
                    ctx.Fail("the clear could not be written down");
                return;
            }
            // One command for each pour of theirs the ledger knows; nothing of theirs there is nothing to clear.
            var ledger = FluidSpace.Ensure(ctx.World)?.LedgerOf(target);
            if (ledger == null)
            {
                ctx.Fail("the clear could not be written down");
                return;
            }
            foreach (int index in FluidRequestJudge.OriginsOf(ledger, origin.ReferenceID.RawValue))
                FluidScriptReach.Command(target, FluidCommandKind.ClearOrigin, float3.Zero, 0f, 0f, index);
            return;
        }
        // Off the host only the author's own fluid can be named; anyone else's goes as a removal of everything,
        // which the host lets through for an editor or a moderator.
        bool own = origin != null && ReferenceEquals(origin, ctx.Principal.Author);
        var request = own
            ? new FluidRequest(FluidRequestKind.ClearMine, target.ReferenceID.RawValue, float3.Zero, 0f, 0f, 0)
            : new FluidRequest(FluidRequestKind.Remove, target.ReferenceID.RawValue, float3.Zero, float.MaxValue, 0f, 0);
        FluidScriptReach.Ask(ctx, request);
    }
}
