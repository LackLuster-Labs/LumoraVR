// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Scripting;
using Lumora.Script;

namespace Lumora.Fluids;

// How deep the settled fluid is over point, in metres, the slot of the substance it is, and its colour, from the
// ledger, so the answer matches on every machine. Nothing there reads as depth 0. Counts as one of the frame's fluid
// reads.
[Node("Physics/Fluids/Fluid At", Capability.Physics, Description = "Reserved for reading settled fluid depth at a point. Point sampling is not implemented yet; this currently returns zero.")]
public sealed class FluidAt : ReadNode
{
    [return: Port("depth")]
    public static float Run(ScriptContext ctx, Slot? system, float3 point, out Slot? substance, out colorHDR tint)
    {
        substance = null;
        tint = default;
        FluidScriptReach.CountSphereRead(ctx);
        if (!FluidScriptReach.Finite(point))
            return 0f;
        var one = FluidScriptReach.System(ctx, system, fail: false);
        if (one == null || one.Substances.Count == 0)
            return 0f;
        return 0f;
    }
}
