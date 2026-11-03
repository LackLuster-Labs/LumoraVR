// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// One step of easing current toward target: each step closes 1 - e^(-speed * delta) of the gap, so it
// slows as it nears, and the same speed eases alike at any frame rate, where a plain lerp by a fixed
// share per frame would ease faster on a faster machine. Wired from On Update, with a Stored Value
// holding current and the event's delta, it follows target smoothly. Vectors and colors go straight; a
// rotation turns the shorter way. A speed or delta below zero stands still, and a current that is not a
// number jumps to target. -xlinka
[Node("Math/Interpolation/Smooth Toward", Description = "One step of moving a value partway toward a target, slowing as it nears, for delta seconds. Smooth Follow does the same by itself every frame.",
    Keywords = "damp, exponential smoothing, ease toward")]
public sealed class SmoothToward : ValueNode
{
    public static float Run(float current, float target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current) || k >= 1f ? target : current + (target - current) * k;
    }

    public static double Run(double current, double target, double speed = 4d, double delta = 0d)
    {
        double rate = speed * delta;
        double k = rate > 0d ? 1d - Math.Exp(-rate) : 0d;
        return double.IsNaN(current) || k >= 1d ? target : current + (target - current) * k;
    }

    public static float2 Run(float2 current, float2 target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current.x + current.y) || k >= 1f ? target : current + (target - current) * k;
    }

    public static float3 Run(float3 current, float3 target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current.x + current.y + current.z) || k >= 1f ? target : current + (target - current) * k;
    }

    public static float4 Run(float4 current, float4 target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current.x + current.y + current.z + current.w) || k >= 1f ? target : current + (target - current) * k;
    }

    public static color Run(color current, color target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current.r + current.g + current.b + current.a) || k >= 1f ? target : current + (target - current) * k;
    }

    public static colorHDR Run(colorHDR current, colorHDR target, float speed = 4f, float delta = 0f)
    {
        float k = Share(speed, delta);
        return float.IsNaN(current.r + current.g + current.b + current.a) || k >= 1f ? target : current + (target - current) * k;
    }

    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ current, [Default(0, 0, 0, 1)] floatQ target, float speed = 4f, float delta = 0f)
    {
        var to = Turns.Unit(target);
        float k = Share(speed, delta);
        if (float.IsNaN(current.x + current.y + current.z + current.w) || k >= 1f)
            return to;
        return SphericalBlend.Rotations(current, to, k);
    }

    // The share of the gap one step closes.
    private static float Share(float speed, float delta)
    {
        float rate = speed * delta;
        return rate > 0f ? 1f - MathF.Exp(-rate) : 0f;
    }
}
