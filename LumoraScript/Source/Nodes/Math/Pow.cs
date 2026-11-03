// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Vectors and colors go lane by lane: each lane of value to the same lane of power. A vector or a color to
// a number raises every lane to it, and a number to a vector or a color is raised to each lane in turn. A
// rotation to a power turns that many times as far about the same axis, the shorter way round: 0.5 is half
// the turn, 2 twice it and -1 its undoing. An unwired power is 2, a square, in every lane of a vector or a
// color power too, and 1 for a rotation, which leaves it as it is. -xlinka
[Node("Math/Pow", Description = "Raises value to a power: 2 to the power 3 is 8. Vectors and colors go lane by lane; a rotation to a power turns that many times as far.",
    Keywords = "power, exponent, raise, square, squared")]
public sealed class Pow : ValueNode
{
    public static float Run(float value, float power = 2f) => MathF.Pow(value, power);
    public static double Run(double value, double power = 2d) => Math.Pow(value, power);
    public static float2 Run(float2 value, [Default(2)] float2 power) => new(Run(value.x, power.x), Run(value.y, power.y));
    public static float3 Run(float3 value, [Default(2)] float3 power) => new(Run(value.x, power.x), Run(value.y, power.y), Run(value.z, power.z));

    public static float4 Run(float4 value, [Default(2)] float4 power)
        => new(Run(value.x, power.x), Run(value.y, power.y), Run(value.z, power.z), Run(value.w, power.w));

    public static color Run(color value, [Default(2)] color power)
        => new(Run(value.r, power.r), Run(value.g, power.g), Run(value.b, power.b), Run(value.a, power.a));

    public static colorHDR Run(colorHDR value, [Default(2)] colorHDR power)
        => new(Run(value.r, power.r), Run(value.g, power.g), Run(value.b, power.b), Run(value.a, power.a));

    public static color Run(color value, float power = 2f)
        => new(Run(value.r, power), Run(value.g, power), Run(value.b, power), Run(value.a, power));

    public static colorHDR Run(colorHDR value, float power = 2f)
        => new(Run(value.r, power), Run(value.g, power), Run(value.b, power), Run(value.a, power));

    public static float2 Run(float2 value, float power = 2f) => new(Run(value.x, power), Run(value.y, power));
    public static float3 Run(float3 value, float power = 2f) => new(Run(value.x, power), Run(value.y, power), Run(value.z, power));

    public static float4 Run(float4 value, float power = 2f)
        => new(Run(value.x, power), Run(value.y, power), Run(value.z, power), Run(value.w, power));

    public static float2 Run(float value, [Default(2)] float2 power) => new(Run(value, power.x), Run(value, power.y));
    public static float3 Run(float value, [Default(2)] float3 power) => new(Run(value, power.x), Run(value, power.y), Run(value, power.z));

    public static float4 Run(float value, [Default(2)] float4 power)
        => new(Run(value, power.x), Run(value, power.y), Run(value, power.z), Run(value, power.w));

    public static color Run(float value, [Default(2)] color power)
        => new(Run(value, power.r), Run(value, power.g), Run(value, power.b), Run(value, power.a));

    public static colorHDR Run(float value, [Default(2)] colorHDR power)
        => new(Run(value, power.r), Run(value, power.g), Run(value, power.b), Run(value, power.a));

    public static floatQ Run(floatQ value, float power = 1f)
    {
        RotationMath.ToAxis(value, out double x, out double y, out double z, out double radians);
        return RotationMath.FromAxis(x, y, z, radians * power);
    }

    public static ScriptBigInt Run(ScriptBigInt? value, int power = 2) => ScriptBigInt.Pow(value, power);
}
