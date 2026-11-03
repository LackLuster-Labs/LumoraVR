// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws a sphere as its three rings, one round each axis, for seconds, one frame for 0. Drawn on the machine
// the script runs on and nowhere else, as Draw Line is. The sphere is in space's coordinates when a space is
// given, else the world's, so a scaled space stretches it. A radius of 0 or less draws nothing, and an unset
// colour draws white.
[Node("Debug/Drawing/Draw Sphere", Description = "Draws a sphere as its three rings, one round each axis, for seconds, one frame for 0.")]
public sealed class DrawSphere : ActionNode
{
    public static void Run(ScriptContext ctx, float3 center, [Default(1)] color color, float radius = 0.1f, float thickness = ScriptDrawings.LineRadius,
        float seconds = 0f, Slot? space = null)
    {
        if (!(radius > 0f) || !float.IsFinite(radius))
            return;
        var lines = ScriptDrawings.Scratch();
        ScriptDrawingShapes.Rings(center, radius, floatQ.Identity, lines);
        ScriptDrawings.Lines(ctx, lines, color, thickness, seconds, space);
    }
}
