// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// Pours litres of fluid into a system at point, thrown at velocity, spread over a ball of spread metres, of the
// substance on the slot given (the system's first when none). A Shared script, or any script running on the host,
// has the host write it down for every machine to simulate; a person's own script on their machine asks the host,
// which lets it through when they may spawn things and the world lets them pour. A Local script pours on this
// machine only, never counted, never seen by anyone else. It always pours: past 50 litres in a second from one
// script it warns, and nothing caps it. -xlinka
[Node("Physics/Fluids/Emit Fluid", Capability.Physics, Description = "Pours a chosen amount of fluid at a point, with an initial velocity and spread.")]
public sealed class EmitFluid : ActionNode
{
    // A Local script's pour, for this machine's own simulation alone. Raised on the world's thread.
    public static event Action<FluidSystem, FluidCommand>? Local;

    public static void Run(ScriptContext ctx, Slot? system, float3 point, float3 velocity, float litres = 0.1f, Slot? substance = null, float spread = 0.1f)
    {
        var target = FluidScriptReach.System(ctx, system, fail: true);
        if (target == null)
            return;
        if (!FluidScriptReach.Finite(point) || !FluidScriptReach.Finite(velocity))
        {
            ctx.Fail("the point or velocity is not a finite number");
            return;
        }
        if (!float.IsFinite(litres) || litres <= 0f)
        {
            ctx.Fail("litres has to be more than nothing");
            return;
        }
        if (!float.IsFinite(spread) || spread < 0f)
            spread = 0f;
        FluidScriptReach.CountEmit(ctx, litres);
        int index = FluidScriptReach.SubstanceIndex(target, substance);
        var local = target.Slot.GlobalPointToLocal(point);
        var along = target.Slot.GlobalDirectionToLocal(velocity);

        if (ctx.IsLocal)
        {
            int seed = HashCode.Combine(target.ReferenceID.RawValue, ctx.World.Time.UpdateIndex, point.x, point.y, point.z);
            Local?.Invoke(target, new FluidCommand(FluidCommandKind.Emit, FluidCommand.AnyOrigin, local, spread, litres, index, seed, along));
            return;
        }
        if (FluidScriptReach.OnHost(ctx))
        {
            var author = ctx.Principal.IsWorldContent ? null : ctx.Principal.Author;
            if (author != null && !FluidTool.MayPour(ctx.World, author, target))
            {
                ctx.Fail($"{FluidScriptReach.Named(author)} may not pour fluid in this world");
                return;
            }
            if (!FluidScriptReach.Command(target, FluidCommandKind.Emit, point, spread, litres))
                ctx.Fail("the pour could not be written down");
            return;
        }
        FluidScriptReach.Ask(ctx, new FluidRequest(FluidRequestKind.Emit, target.ReferenceID.RawValue, local, spread, litres, 0, index, along));
    }
}
