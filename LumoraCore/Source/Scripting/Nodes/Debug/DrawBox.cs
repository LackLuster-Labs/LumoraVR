// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Components.Scripting;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws the twelve edges of a box for seconds, one frame for 0: size edge to edge, turned by rotation about
// its centre. Drawn on the machine the script runs on and nowhere else, as Draw Line is. The box is in
// space's coordinates when a space is given, else the world's, so a scaled space stretches it. An unset
// rotation turns nothing and an unset colour draws white.
[Node("Debug/Drawing/Draw Box", Description = "Draws the twelve edges of a box for seconds, one frame for 0: size edge to edge, turned by rotation about its centre.")]
public sealed class DrawBox : ActionNode
{
    public static void Run(ScriptContext ctx, float3 center, [Default(0.2)] float3 size, [Default(0, 0, 0, 1)] floatQ rotation, [Default(1)] color color,
        float radius = ScriptDrawings.LineRadius, float seconds = 0f, Slot? space = null)
    {
        var lines = ScriptDrawings.Scratch();
        ScriptDrawingShapes.BoxEdges(center, size, ScriptDrawings.Turn(rotation), lines);
        ScriptDrawings.Lines(ctx, lines, color, radius, seconds, space);
    }
}
