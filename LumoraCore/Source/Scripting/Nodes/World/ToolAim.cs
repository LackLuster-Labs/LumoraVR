// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// What the tool on the slot points at: a ray cast as it is read, from the tool's tip the way it points, no further
// than range metres, triggers only when asked, passing over the tool itself and the body of whoever holds
// it. It is worked out from where the tool is on this machine, which for someone else's tool is where it was
// when its pose last arrived, not the beam their machine draws. It counts as one of the script's rays.
[Node("Interaction/Tools/Tool Aim", Capability.Physics, Description = "Casts a ray from a tool's tip and reports the object and point it is aimed at.")]
public sealed class ToolAim : ReadNode
{
    [return: Port("hit")]
    public static bool Run(ScriptContext ctx, Slot? slot, out Slot? struck, out float3 point,
        out float3 normal, out float distance, float range = 10f, bool triggers = false)
    {
        if (!ScriptReads.Visible(ctx, slot) || slot!.GetComponent<ToolItem>() is not { IsDestroyed: false } tool)
            return ScriptPhysics.Report(ctx, false, default, out struck, out point, out normal, out distance);
        var origin = tool.Tip;
        var along = tool.TipForward;
        if (!ScriptPhysics.CanCast(origin, along, range))
            return ScriptPhysics.Report(ctx, false, default, out struck, out point, out normal, out distance);
        ctx.CountCast();
        var ignoring = new List<Slot>(2) { slot };
        if (slot.ActiveUserRoot?.Slot is { IsDestroyed: false } body)
            ignoring.Add(body);
        bool hit = ScriptPhysics.StrikeFirst(ctx, origin, along.Normalized, range, triggers, ignoring, out PhysicsRaycastHit found);
        return ScriptPhysics.Report(ctx, hit, found, out struck, out point, out normal, out distance);
    }
}
