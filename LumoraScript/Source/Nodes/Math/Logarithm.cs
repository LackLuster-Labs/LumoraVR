// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The power that turns base into value: 8 in base 2 is 3. Base defaults to e, the natural logarithm; bases 10
// and 2 are worked out exactly, so 1000 in base 10 is 3 and not a hair under. 0 gives -infinity and below 0
// NaN, since no power reaches them, and a base of 1, 0 or below has no powers to count in and gives NaN.
// Vectors and colors go lane by lane; a number for base serves every lane, and a number for value is taken
// in each lane's base.
[Node("Math/Log", Description = "The power that turns base into value: 8 in base 2 is 3.",
    Keywords = "ln, natural log, log10, log2, logarithm base")]
public sealed class Logarithm : ValueNode
{
    public static float Run(float value, float @base = MathF.E)
    {
        if (@base == MathF.E)
            return MathF.Log(value);
        if (@base == 10f)
            return MathF.Log10(value);
        if (@base == 2f)
            return MathF.Log2(value);
        return (float)Of(value, @base);
    }

    public static double Run(double value, double @base = Math.E)
    {
        if (@base == Math.E)
            return Math.Log(value);
        if (@base == 10d)
            return Math.Log10(value);
        if (@base == 2d)
            return Math.Log2(value);
        return Of(value, @base);
    }

    public static float2 Run(float2 value, float @base = MathF.E) => new(Run(value.x, @base), Run(value.y, @base));
    public static float3 Run(float3 value, float @base = MathF.E) => new(Run(value.x, @base), Run(value.y, @base), Run(value.z, @base));

    public static float4 Run(float4 value, float @base = MathF.E)
        => new(Run(value.x, @base), Run(value.y, @base), Run(value.z, @base), Run(value.w, @base));

    public static float2 Run(float2 value, [Default(Math.E)] float2 @base) => new(Run(value.x, @base.x), Run(value.y, @base.y));
    public static float3 Run(float3 value, [Default(Math.E)] float3 @base) => new(Run(value.x, @base.x), Run(value.y, @base.y), Run(value.z, @base.z));

    public static float4 Run(float4 value, [Default(Math.E)] float4 @base)
        => new(Run(value.x, @base.x), Run(value.y, @base.y), Run(value.z, @base.z), Run(value.w, @base.w));

    public static float2 Run(float value, [Default(Math.E)] float2 @base) => new(Run(value, @base.x), Run(value, @base.y));
    public static float3 Run(float value, [Default(Math.E)] float3 @base) => new(Run(value, @base.x), Run(value, @base.y), Run(value, @base.z));

    public static float4 Run(float value, [Default(Math.E)] float4 @base)
        => new(Run(value, @base.x), Run(value, @base.y), Run(value, @base.z), Run(value, @base.w));

    public static color Run(color value, float @base = MathF.E)
        => new(Run(value.r, @base), Run(value.g, @base), Run(value.b, @base), Run(value.a, @base));

    public static colorHDR Run(colorHDR value, float @base = MathF.E)
        => new(Run(value.r, @base), Run(value.g, @base), Run(value.b, @base), Run(value.a, @base));

    public static color Run(color value, [Default(Math.E)] color @base)
        => new(Run(value.r, @base.r), Run(value.g, @base.g), Run(value.b, @base.b), Run(value.a, @base.a));

    public static colorHDR Run(colorHDR value, [Default(Math.E)] colorHDR @base)
        => new(Run(value.r, @base.r), Run(value.g, @base.g), Run(value.b, @base.b), Run(value.a, @base.a));

    public static color Run(float value, [Default(Math.E)] color @base)
        => new(Run(value, @base.r), Run(value, @base.g), Run(value, @base.b), Run(value, @base.a));

    public static colorHDR Run(float value, [Default(Math.E)] colorHDR @base)
        => new(Run(value, @base.r), Run(value, @base.g), Run(value, @base.b), Run(value, @base.a));

    private static double Of(double value, double @base) => @base > 0d && @base != 1d ? Math.Log(value) / Math.Log(@base) : double.NaN;
}
