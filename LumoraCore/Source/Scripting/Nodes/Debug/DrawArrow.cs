// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws a vector as an arrow from start to start plus vector for seconds, one frame for 0: a rod radius
// metres thick with a pointed head a fifth of its length. Drawn on the machine the script runs on and
// nowhere else, as Draw Line is. start and vector are in space's coordinates when a space is given, else the
// world's. A vector of no length draws nothing, and an unset colour draws white.
[Node("Debug/Drawing/Draw Arrow", Description = "Draws a vector as an arrow from start to start plus vector for seconds, one frame for 0: a rod radius metres thick with a pointed head a fifth of its length.")]
public sealed class DrawArrow : ActionNode
{
    public static void Run(ScriptContext ctx, float3 start, [Default(0, 0, 0.2)] float3 vector, [Default(1)] color color, float radius = ScriptDrawings.LineRadius,
        float seconds = 0f, Slot? space = null)
        => ScriptDrawings.Arrow(ctx, start, vector, color, radius, seconds, space);
}
