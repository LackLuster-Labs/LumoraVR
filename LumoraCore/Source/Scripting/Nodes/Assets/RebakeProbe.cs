// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Captures what a reflection probe sees again, on every machine, the way its Rebake button does. Each
// capture is six scene renders, so it is for after the room has changed, not for every frame. Judged by
// the gate like a write to the probe.
[Node("Rendering/Rebake Probe", Capability.WorldWrite, Description = "Captures what a reflection probe sees again, on every machine, the way its Rebake button does.",
    Keywords = "update reflections, refresh probe, bake reflections")]
public sealed class RebakeProbe : ActionNode
{
    public static void Run(ScriptContext ctx, ReflectionProbe? probe)
    {
        if (!ScriptReads.Reached(ctx, probe, "probe"))
            return;
        ScriptWrites.Check(ctx, probe!.BakeGeneration);
        probe.Rebake();
    }
}
