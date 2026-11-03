// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws a filled triangle through three points for seconds, one frame for 0, seen from both sides. Drawn on
// the machine the script runs on and nowhere else, as Draw Line is. Points are in space's coordinates when a
// space is given, else the world's. An unset colour draws white, and a point that is not a finite number
// draws nothing.
[Node("Debug/Drawing/Draw Triangle", Description = "Draws a filled triangle through three points for seconds, one frame for 0, seen from both sides.")]
public sealed class DrawTriangle : ActionNode
{
    public static void Run(ScriptContext ctx, float3 a, float3 b, float3 c, [Default(1)] color color, float seconds = 0f, Slot? space = null)
    {
        ReadOnlySpan<float3> points = stackalloc float3[] { a, b, c };
        ScriptDrawings.Faces(ctx, points, color, seconds, space);
    }
}
