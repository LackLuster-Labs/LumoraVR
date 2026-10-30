// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Takes fluid out of a system within radius metres of center: at most litres, or all of it there when litres is 0.
// On the host it goes from the ledger at once and every machine follows, and removed is what went. Anywhere else it
// is asked for, the host lets it through for your own fluid, a system you may edit, or a moderator, and removed is
// what was asked for. A Local script cannot take fluid away from everyone, so it fails.
[Node("Physics/Fluids/Remove Fluid", Capability.Physics, Description = "Removes fluid within a sphere, up to the requested litres. A limit of zero removes all fluid in that area.")]
public sealed class RemoveFluid : ActionNode
{
    [return: Port("removed")]
    public static float Run(ScriptContext ctx, Slot? system, float3 center, float radius = 0.1f, float litres = 0f)
    {
        var target = FluidScriptReach.System(ctx, system, fail: true);
        if (target == null)
            return 0f;
        if (!FluidScriptReach.Finite(center) || !float.IsFinite(radius) || radius <= 0f || !float.IsFinite(litres) || litres < 0f)
        {
            ctx.Fail("the center, radius or litres is not usable");
            return 0f;
        }
        if (ctx.IsLocal)
        {
            ctx.Fail("a Local script cannot take fluid away from everyone");
            return 0f;
        }
        float there = target.LitresIn(center, radius);
        float asked = litres > 0f ? litres : there;
        if (FluidScriptReach.OnHost(ctx))
        {
            if (!FluidScriptReach.Command(target, FluidCommandKind.Remove, center, radius, litres))
            {
                ctx.Fail("the removal could not be written down");
                return 0f;
            }
            return MathF.Min(asked, there);
        }
        var local = target.Slot.GlobalPointToLocal(center);
        return FluidScriptReach.Ask(ctx, new FluidRequest(FluidRequestKind.Remove, target.ReferenceID.RawValue, local, radius, litres, 0)) ? asked : 0f;
    }
}
