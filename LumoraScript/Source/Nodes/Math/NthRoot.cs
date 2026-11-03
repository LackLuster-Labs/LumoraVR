// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The number that raised to the power n gives value: n of 2, the default, is the square root and 3 the cube
// root. Below 0 an odd whole n still has a root, the negative one, so the cube root of -8 is -2; any other n
// has none there and gives NaN, and so does an n of 0. Vectors and colors go lane by lane; a number for n
// serves every lane.
[Node("Math/Nth Root", Description = "The number that raised to the power n gives value: n of 2, the default, is the square root and 3 the cube root.",
    Keywords = "cube root, cbrt, root")]
public sealed class NthRoot : ValueNode
{
    public static float Run(float value, float n = 2f) => (float)Run((double)value, (double)n);

    public static double Run(double value, double n = 2d)
    {
        if (n == 2d)
            return Math.Sqrt(value);
        if (n == 3d)
            return Math.Cbrt(value);
        if (n == 0d)
            return double.NaN;
        if (value < 0d && double.IsOddInteger(n))
            return -Math.Pow(-value, 1d / n);
        return Math.Pow(value, 1d / n);
    }

    public static float2 Run(float2 value, float n = 2f) => new(Run(value.x, n), Run(value.y, n));
    public static float3 Run(float3 value, float n = 2f) => new(Run(value.x, n), Run(value.y, n), Run(value.z, n));
    public static float4 Run(float4 value, float n = 2f) => new(Run(value.x, n), Run(value.y, n), Run(value.z, n), Run(value.w, n));
    public static float2 Run(float2 value, [Default(2)] float2 n) => new(Run(value.x, n.x), Run(value.y, n.y));
    public static float3 Run(float3 value, [Default(2)] float3 n) => new(Run(value.x, n.x), Run(value.y, n.y), Run(value.z, n.z));
    public static float4 Run(float4 value, [Default(2)] float4 n) => new(Run(value.x, n.x), Run(value.y, n.y), Run(value.z, n.z), Run(value.w, n.w));
    public static color Run(color value, float n = 2f) => new(Run(value.r, n), Run(value.g, n), Run(value.b, n), Run(value.a, n));
    public static colorHDR Run(colorHDR value, float n = 2f) => new(Run(value.r, n), Run(value.g, n), Run(value.b, n), Run(value.a, n));
    public static color Run(color value, [Default(2)] color n) => new(Run(value.r, n.r), Run(value.g, n.g), Run(value.b, n.b), Run(value.a, n.a));

    public static colorHDR Run(colorHDR value, [Default(2)] colorHDR n)
        => new(Run(value.r, n.r), Run(value.g, n.g), Run(value.b, n.b), Run(value.a, n.a));
}
