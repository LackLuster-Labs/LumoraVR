// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Bounces between 0 and length as value runs on: up from 0 to length, back down to 0 by twice length, and so
// on for ever, the same way below 0, so it never leaves 0 to length. Whole numbers turn at 0 and at length
// alike. A length of 0 or below gives 0; length defaults to 1. Vectors and colors go lane by lane; a number
// for length serves every lane.
[Node("Math/Shaping/Ping Pong", Description = "Bounces between 0 and length as value runs on: up from 0 to length, back down to 0 by twice length, and so on for ever, the same way below 0, so it never leaves 0 to length.",
    Keywords = "bounce, triangle wave, back and forth, oscillate")]
public sealed class PingPong : ValueNode
{
    [Gpu("({length} > 0.0 ? {length} - abs(mod({value}, 2.0 * {length}) - {length}) : 0.0)")]
    public static float Run(float value, float length = 1f) => (float)Run((double)value, (double)length);

    public static double Run(double value, double length = 1d)
    {
        if (!(length > 0d))
            return 0d;
        double period = 2d * length;
        if (double.IsInfinity(period))
            return Math.Min(Math.Abs(value), length);
        double wrapped = Repeat.Run(value, period);
        return wrapped <= length ? wrapped : period - wrapped;
    }

    public static int Run(int value, int length = 1) => length <= 0 ? 0 : (int)Bounce(value, length);
    public static long Run(long value, long length = 1L) => length <= 0L ? 0L : (long)Bounce(value, length);
    public static uint Run(uint value, uint length = 1u) => length == 0u ? 0u : (uint)Bounce(value, length);
    public static ulong Run(ulong value, ulong length = 1ul) => length == 0ul ? 0ul : (ulong)Bounce(value, length);
    [Gpu("({length} > 0.0 ? {length} - abs(mod({value}, 2.0 * {length}) - {length}) : vec2(0.0))")]
    public static float2 Run(float2 value, float length = 1f) => new(Run(value.x, length), Run(value.y, length));
    [Gpu("({length} > 0.0 ? {length} - abs(mod({value}, 2.0 * {length}) - {length}) : vec3(0.0))")]
    public static float3 Run(float3 value, float length = 1f) => new(Run(value.x, length), Run(value.y, length), Run(value.z, length));

    [Gpu("({length} > 0.0 ? {length} - abs(mod({value}, 2.0 * {length}) - {length}) : vec4(0.0))")]
    public static float4 Run(float4 value, float length = 1f)
        => new(Run(value.x, length), Run(value.y, length), Run(value.z, length), Run(value.w, length));

    public static float2 Run(float2 value, [Default(1)] float2 length) => new(Run(value.x, length.x), Run(value.y, length.y));
    public static float3 Run(float3 value, [Default(1)] float3 length) => new(Run(value.x, length.x), Run(value.y, length.y), Run(value.z, length.z));

    public static float4 Run(float4 value, [Default(1)] float4 length)
        => new(Run(value.x, length.x), Run(value.y, length.y), Run(value.z, length.z), Run(value.w, length.w));

    public static int2 Run(int2 value, int length = 1) => new(Run(value.x, length), Run(value.y, length));
    public static int3 Run(int3 value, int length = 1) => new(Run(value.x, length), Run(value.y, length), Run(value.z, length));

    public static int4 Run(int4 value, int length = 1)
        => new(Run(value.x, length), Run(value.y, length), Run(value.z, length), Run(value.w, length));

    public static int2 Run(int2 value, [Default(1)] int2 length) => new(Run(value.x, length.x), Run(value.y, length.y));
    public static int3 Run(int3 value, [Default(1)] int3 length) => new(Run(value.x, length.x), Run(value.y, length.y), Run(value.z, length.z));

    public static int4 Run(int4 value, [Default(1)] int4 length)
        => new(Run(value.x, length.x), Run(value.y, length.y), Run(value.z, length.z), Run(value.w, length.w));

    public static color Run(color value, float length = 1f)
        => new(Run(value.r, length), Run(value.g, length), Run(value.b, length), Run(value.a, length));

    public static colorHDR Run(colorHDR value, float length = 1f)
        => new(Run(value.r, length), Run(value.g, length), Run(value.b, length), Run(value.a, length));

    public static color Run(color value, [Default(1)] color length)
        => new(Run(value.r, length.r), Run(value.g, length.g), Run(value.b, length.b), Run(value.a, length.a));

    public static colorHDR Run(colorHDR value, [Default(1)] colorHDR length)
        => new(Run(value.r, length.r), Run(value.g, length.g), Run(value.b, length.b), Run(value.a, length.a));

    // A whole-number bounce, worked out wide so twice the length never overflows. Length is above 0.
    private static Int128 Bounce(Int128 value, Int128 length)
    {
        Int128 period = 2 * length;
        Int128 wrapped = value % period;
        if (wrapped < 0)
            wrapped += period;
        return wrapped <= length ? wrapped : period - wrapped;
    }
}
