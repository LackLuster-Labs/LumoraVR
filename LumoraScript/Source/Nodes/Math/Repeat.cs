// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Wraps value into 0 up to length, the way an angle wraps into a turn: 370 by 360 is 10, and -10 is 350 where
// Remainder would keep -10. The answer is never length itself. Length defaults to 1, which keeps what is past
// the whole number below, so -0.25 gives 0.75. A length of 0 gives 0, and one below 0 wraps into length up
// to 0. Vectors and colors go lane by lane; a number for length serves every lane.
[Node("Math/Shaping/Repeat", Description = "Wraps value into 0 up to length, the way an angle wraps into a turn: 370 by 360 is 10, and -10 is 350 where Remainder would keep -10.",
    Keywords = "wrap, loop, cycle, modulo")]
public sealed class Repeat : ValueNode
{
    [Gpu("({length} == 0.0 ? 0.0 : {value} - {length} * floor({value} / {length}))")]
    public static float Run(float value, float length = 1f)
    {
        if (length == 0f)
            return 0f;
        float wrapped = value % length;
        if (wrapped == 0f)
            return 0f;
        if ((wrapped < 0f) != (length < 0f))
            wrapped += length;
        return wrapped == length ? 0f : wrapped;
    }

    public static double Run(double value, double length = 1d)
    {
        if (length == 0d)
            return 0d;
        double wrapped = value % length;
        if (wrapped == 0d)
            return 0d;
        if ((wrapped < 0d) != (length < 0d))
            wrapped += length;
        return wrapped == length ? 0d : wrapped;
    }

    // A length of -1 wraps everything to 0, and is kept from the remainder, which the smallest int over -1
    // would overflow.
    public static int Run(int value, int length = 1)
    {
        if (length == 0 || length == -1)
            return 0;
        int wrapped = value % length;
        return wrapped != 0 && (wrapped < 0) != (length < 0) ? wrapped + length : wrapped;
    }

    public static long Run(long value, long length = 1L)
    {
        if (length == 0L || length == -1L)
            return 0L;
        long wrapped = value % length;
        return wrapped != 0L && (wrapped < 0L) != (length < 0L) ? wrapped + length : wrapped;
    }

    public static uint Run(uint value, uint length = 1u) => length == 0u ? 0u : value % length;
    public static ulong Run(ulong value, ulong length = 1ul) => length == 0ul ? 0ul : value % length;
    [Gpu("({length} == 0.0 ? vec2(0.0) : {value} - {length} * floor({value} / {length}))")]
    public static float2 Run(float2 value, float length = 1f) => new(Run(value.x, length), Run(value.y, length));
    [Gpu("({length} == 0.0 ? vec3(0.0) : {value} - {length} * floor({value} / {length}))")]
    public static float3 Run(float3 value, float length = 1f) => new(Run(value.x, length), Run(value.y, length), Run(value.z, length));

    [Gpu("({length} == 0.0 ? vec4(0.0) : {value} - {length} * floor({value} / {length}))")]
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
}
