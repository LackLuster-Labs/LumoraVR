// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// Draws the three axes of a rotation at a point as arrows length metres long for seconds, one frame for 0:
// right, up and forward, red, green and blue unless given colours of their own. Drawn on the machine the
// script runs on and nowhere else, as Draw Line is. The point and rotation are in space's coordinates when a
// space is given, else the world's. An unset rotation is none.
[Node("Debug/Drawing/Draw Axes", Description = "Draws the three axes of a rotation at a point as arrows length metres long for seconds, one frame for 0: right, up and forward, red, green and blue unless given colours of their own.")]
public sealed class DrawAxes : ActionNode
{
    public static void Run(ScriptContext ctx, float3 position, [Default(0, 0, 0, 1)] floatQ rotation, float length = 0.1f,
        [Default(1, 0, 0, 1)] color right = default, [Default(0, 1, 0, 1)] color up = default,
        [Default(0, 0, 1, 1)] color forward = default, float seconds = 0f, Slot? space = null)
    {
        if (!(length > 0f) || !float.IsFinite(length))
            return;
        var turn = ScriptDrawings.Turn(rotation);
        float radius = length * 0.03f;
        ScriptDrawings.Arrow(ctx, position, turn * float3.Right * length, Or(right, color.Red), radius, seconds, space);
        ScriptDrawings.Arrow(ctx, position, turn * float3.Up * length, Or(up, color.Green), radius, seconds, space);
        ScriptDrawings.Arrow(ctx, position, turn * float3.Forward * length, Or(forward, color.Blue), radius, seconds, space);
    }

    private static color Or(color given, color fallback)
        => given.r == 0f && given.g == 0f && given.b == 0f && given.a == 0f ? fallback : given;
}
