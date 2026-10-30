// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// The litres of fluid within radius metres of center, from the ledger, so every machine and a headless host give the
// same answer. Moving says some of it has not settled. localLitres is what this machine's own simulation holds there,
// about three frames old, and nothing on a machine that only shows fluid. With no system bound it counts every system
// the script can see. Each call counts towards a frame's fluid reads, which warn past 256 and never stop.
[Node("Physics/Fluids/Fluid In Sphere", Capability.Physics, Description = "Reads the recorded litres of fluid within a sphere. The moving and localLitres outputs currently remain zero.")]
public sealed class FluidInSphere : ReadNode
{
    [return: Port("litres")]
    public static float Run(ScriptContext ctx, Slot? system, float3 center, float radius, out bool moving, out float localLitres)
    {
        moving = false;
        localLitres = 0f;
        FluidScriptReach.CountSphereRead(ctx);
        if (!FluidScriptReach.Finite(center) || !float.IsFinite(radius) || radius <= 0f)
            return 0f;
        if (system != null)
        {
            var one = FluidScriptReach.System(ctx, system, fail: false);
            return one != null ? one.LitresIn(center, radius) : 0f;
        }
        float litres = 0f;
        foreach (var each in FluidScriptReach.Systems(ctx))
            litres += each.LitresIn(center, radius);
        return litres;
    }
}
