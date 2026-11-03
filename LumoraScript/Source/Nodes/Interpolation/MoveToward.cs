// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// One step of moving current toward target at speed for delta seconds: at most speed times delta closer
// and never past it, reached once the step lands on target. Wired from On Update, with a Stored Value
// holding current and the event's delta, it moves a value at a steady speed whatever the frame rate.
// Vectors and colors move in a straight line. A rotation turns the shorter way, speed in degrees a
// second. A speed or delta below zero stands still, and a current that is not a number jumps to target,
// so a broken stored value mends itself. reached sits before speed only so speed and delta can have
// defaults; the port order is unchanged. -xlinka
[Node("Math/Interpolation/Move Toward", Description = "One step of moving current toward target at speed for delta seconds: at most speed times delta closer and never past it. Steady Follow does the same by itself every frame.",
    Keywords = "approach, step toward, towards")]
public sealed class MoveToward : ValueNode
{
    public static float Run(float current, float target, out bool reached, float speed = 1f, float delta = 0f)
    {
        float step = Step(speed, delta);
        float gap = target - current;
        reached = !(MathF.Abs(gap) > step);
        return reached ? target : current + MathF.CopySign(step, gap);
    }

    public static double Run(double current, double target, out bool reached, double speed = 1d, double delta = 0d)
    {
        double step = speed * delta;
        if (!(step > 0d))
            step = 0d;
        double gap = target - current;
        reached = !(Math.Abs(gap) > step);
        return reached ? target : current + Math.CopySign(step, gap);
    }

    public static float2 Run(float2 current, float2 target, out bool reached, float speed = 1f, float delta = 0f)
    {
        var v = Straight(new float4(current, 0f, 0f), new float4(target, 0f, 0f), Step(speed, delta), out reached);
        return reached ? target : new float2(v.x, v.y);
    }

    public static float3 Run(float3 current, float3 target, out bool reached, float speed = 1f, float delta = 0f)
    {
        var v = Straight(new float4(current, 0f), new float4(target, 0f), Step(speed, delta), out reached);
        return reached ? target : v.xyz;
    }

    public static float4 Run(float4 current, float4 target, out bool reached, float speed = 1f, float delta = 0f)
    {
        var v = Straight(current, target, Step(speed, delta), out reached);
        return reached ? target : v;
    }

    public static color Run(color current, color target, out bool reached, float speed = 1f, float delta = 0f)
    {
        var v = Straight(new float4(current.r, current.g, current.b, current.a), new float4(target.r, target.g, target.b, target.a),
            Step(speed, delta), out reached);
        return reached ? target : new color(v.x, v.y, v.z, v.w);
    }

    public static colorHDR Run(colorHDR current, colorHDR target, out bool reached, float speed = 1f, float delta = 0f)
    {
        var v = Straight(new float4(current.r, current.g, current.b, current.a), new float4(target.r, target.g, target.b, target.a),
            Step(speed, delta), out reached);
        return reached ? target : new colorHDR(v.x, v.y, v.z, v.w);
    }

    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ current, [Default(0, 0, 0, 1)] floatQ target, out bool reached, float speed = 90f, float delta = 0f)
    {
        var to = Turns.Unit(target);
        if (float.IsNaN(current.x + current.y + current.z + current.w))
        {
            reached = true;
            return to;
        }
        var from = Turns.Unit(current);
        float angle = Turns.Between(from, to);
        float step = Step(speed, delta) * (MathF.PI / 180f);
        reached = !(angle > step);
        return reached ? to : SphericalBlend.Rotations(from, to, step / angle);
    }

    private static float Step(float speed, float delta)
    {
        float step = speed * delta;
        return step > 0f ? step : 0f;
    }

    // A current with a NaN lane is not anywhere, so it goes straight to target.
    private static float4 Straight(float4 current, float4 target, float step, out bool reached)
    {
        var gap = target - current;
        float length = gap.Length;
        reached = float.IsNaN(current.x + current.y + current.z + current.w) || !(length > step);
        return reached ? target : current + gap * (step / length);
    }
}
