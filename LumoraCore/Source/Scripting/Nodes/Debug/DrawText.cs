// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws text at a point for seconds, one frame for 0, size metres a line, centred on the point and turned
// to face whoever is looking on that machine. Drawn on the machine the script runs on and nowhere else, as
// Draw Line is. The point is in space's coordinates when a space is given, else the world's. Empty text
// draws nothing, and an unset colour draws white.
[Node("Debug/Drawing/Draw Text", Description = "Draws text at a point for seconds, one frame for 0, size metres a line, centred on the point and turned to face whoever is looking on that machine.")]
public sealed class DrawText : ActionNode
{
    public static void Run(ScriptContext ctx, float3 position, string? text, [Default(1)] color color, float size = 0.1f, float seconds = 0f,
        Slot? space = null)
        => ScriptDrawings.Label(ctx, position, text, size, color, seconds, space);
}
