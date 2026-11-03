// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Captures again every reflection probe under root that keeps what it captured (updated once, not every
// frame), the way Rebake Probe does one: on every machine, each judged by the gate like a write to it. The
// whole world when root is empty. With a tag only probes whose slot carries exactly that tag, and only active
// and enabled ones unless hidden is on. rebaked says how many went and refused how many the gate turned
// down. Each capture is six scene renders on every machine, so this is for after the room has changed.
[Node("Rendering/Rebake Probes", Capability.WorldWrite, Description = "Captures again every reflection probe under root that keeps what it captured (updated once, not every frame), the way Rebake Probe does one: on every machine, each judged by the gate like a write to it.",
    Keywords = "reflection, bake, refresh reflections, update probes")]
public sealed class RebakeProbesUnder : ActionNode
{
    [return: Port("rebaked")]
    public static int Run(ScriptContext ctx, out int refused, Slot? root = null, string? tag = null, bool hidden = false)
    {
        refused = 0;
        root ??= ctx.World.RootSlot;
        if (!ScriptReads.Reached(ctx, root, "root"))
            return 0;
        var probes = new List<ReflectionProbe>();
        root.GetComponentsInChildren(probes);
        int rebaked = 0;
        foreach (var probe in probes)
        {
            if (!ScriptReads.Visible(ctx, probe) || probe.UpdateMode.Value != ProbeUpdateMode.Once)
                continue;
            if (!hidden && (!probe.Enabled.Value || !probe.Slot.IsActive))
                continue;
            if (!string.IsNullOrEmpty(tag) && probe.Slot.Tag.Value != tag)
                continue;
            try
            {
                ScriptWrites.Check(ctx, probe.BakeGeneration);
                probe.Rebake();
                rebaked++;
            }
            catch (UnauthorizedAccessException)
            {
                refused++;
            }
        }
        return rebaked;
    }
}
