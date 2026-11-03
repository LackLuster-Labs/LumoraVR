// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws a line from one point to another for seconds, one frame for 0, as a rod radius metres thick. Drawn
// on the machine the script runs on and nowhere else: the host for a Shared script, each machine for a Local
// one. Points are in space's coordinates when a space is given, else the world's. An unset colour draws
// white, and a point that is not a finite number draws nothing.
[Node("Debug/Drawing/Draw Line", Description = "Draws a line from one point to another for seconds, one frame for 0, as a rod radius metres thick.")]
public sealed class DrawLine : ActionNode
{
    public static void Run(ScriptContext ctx, float3 from, float3 to, [Default(1)] color color, float radius = ScriptDrawings.LineRadius,
        float seconds = 0f, Slot? space = null)
        => ScriptDrawings.Line(ctx, from, to, color, radius, seconds, space);
}
