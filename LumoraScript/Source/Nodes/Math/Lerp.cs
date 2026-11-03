// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Unclamped: t outside 0 to 1 carries on past the ends.
[Node("Math/Interpolation/Lerp", Description = "Blends from the first value at 0 to the second at 1. Amounts outside that range extend past the endpoints.",
    Keywords = "blend, mix, interpolate, linear")]
public sealed class Lerp : ValueNode
{
    public static float Run(float a, float b, float t) => a + (b - a) * t;
    public static double Run(double a, double b, double t) => a + (b - a) * t;
    public static float3 Run(float3 a, float3 b, float t) => a + (b - a) * t;
    public static float2 Run(float2 a, float2 b, float t) => a + (b - a) * t;
    public static float4 Run(float4 a, float4 b, float t) => a + (b - a) * t;
    public static color Run(color a, color b, float t) => a + (b - a) * t;
    public static colorHDR Run(colorHDR a, colorHDR b, float t) => a + (b - a) * t;

    // Whole numbers land on the nearest one, halves on the even neighbour, and hold at the ends of the
    // type; dates and spans move by whole ticks and hold at the first and last there are. A t that is not
    // a number stays at a.
    public static int Run(int a, int b, float t) => (int)Int128.Clamp(Whole(a, b, t), int.MinValue, int.MaxValue);
    public static long Run(long a, long b, float t) => (long)Int128.Clamp(Whole(a, b, t), long.MinValue, long.MaxValue);
    public static int2 Run(int2 a, int2 b, float t) => new(Run(a.x, b.x, t), Run(a.y, b.y, t));
    public static int3 Run(int3 a, int3 b, float t) => new(Run(a.x, b.x, t), Run(a.y, b.y, t), Run(a.z, b.z, t));
    public static int4 Run(int4 a, int4 b, float t) => new(Run(a.x, b.x, t), Run(a.y, b.y, t), Run(a.z, b.z, t), Run(a.w, b.w, t));
    public static DateTime Run(DateTime a, DateTime b, float t) => ClampedTime.Date(Whole(a.Ticks, b.Ticks, t), a.Kind);
    public static TimeSpan Run(TimeSpan a, TimeSpan b, float t) => ClampedTime.Span(Whole(a.Ticks, b.Ticks, t));

    public static Rect Run(Rect a, Rect b, float t)
        => new(Run(a.x, b.x, t), Run(a.y, b.y, t), Run(a.width, b.width, t), Run(a.height, b.height, t));

    public static float2x2 Run(float2x2 a, float2x2 b, float t) => a + (b - a) * t;
    public static float3x3 Run(float3x3 a, float3x3 b, float t) => a + (b - a) * t;

    public static float4x4 Run(float4x4 a, float4x4 b, float t)
        => new(Run(a.c0, b.c0, t), Run(a.c1, b.c1, t), Run(a.c2, b.c2, t), Run(a.c3, b.c3, t));

    // A rotation blends the shorter way round at an even rate, as Slerp does, and always at length one.
    public static floatQ Run(floatQ a, floatQ b, float t) => SphericalBlend.Rotations(a, b, t);

    // Worked from the nearer end, so t of 0 and 1 land exactly on a and b however far apart they are, and
    // rounded on the whole result rather than the step.
    private static Int128 Whole(long a, long b, float t)
    {
        if (float.IsNaN(t))
            return a;
        bool nearA = t <= 0.5f;
        double part = Math.Clamp((double)((Int128)b - a) * (nearA ? t : t - 1f), -4e19, 4e19);
        double floor = Math.Floor(part);
        Int128 whole = (nearA ? a : b) + (Int128)floor;
        double fraction = part - floor;
        if (fraction > 0.5 || (fraction == 0.5 && !Int128.IsEvenInteger(whole)))
            whole++;
        return whole;
    }
}
