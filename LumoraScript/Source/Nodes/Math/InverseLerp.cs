// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Where value sits between a and b: 0 at a, 1 at b, unclamped. a equal to b gives 0. Vectors and colors
// go lane by lane. a and b default to 0 and 1, in every lane of a vector or a color.
[Node("Math/Interpolation/Inverse Lerp", Description = "Where value sits between a and b: 0 at a, 1 at b, unclamped. a equal to b gives 0.",
    Keywords = "unlerp, percent, progress, fraction")]
public sealed class InverseLerp : ValueNode
{
    [return: Port("t")] public static float Run(float a = 0f, float b = 1f, float value = 0f) => a == b ? 0f : (value - a) / (b - a);
    [return: Port("t")] public static double Run(double a = 0d, double b = 1d, double value = 0d) => a == b ? 0d : (value - a) / (b - a);

    [return: Port("t")]
    public static float2 Run(float2 a, [Default(1)] float2 b, float2 value) => new(Run(a.x, b.x, value.x), Run(a.y, b.y, value.y));

    [return: Port("t")]
    public static float3 Run(float3 a, [Default(1)] float3 b, float3 value)
        => new(Run(a.x, b.x, value.x), Run(a.y, b.y, value.y), Run(a.z, b.z, value.z));

    [return: Port("t")]
    public static float4 Run(float4 a, [Default(1)] float4 b, float4 value)
        => new(Run(a.x, b.x, value.x), Run(a.y, b.y, value.y), Run(a.z, b.z, value.z), Run(a.w, b.w, value.w));

    [return: Port("t")]
    public static color Run(color a, [Default(1)] color b, color value)
        => new(Run(a.r, b.r, value.r), Run(a.g, b.g, value.g), Run(a.b, b.b, value.b), Run(a.a, b.a, value.a));

    [return: Port("t")]
    public static colorHDR Run(colorHDR a, [Default(1)] colorHDR b, colorHDR value)
        => new(Run(a.r, b.r, value.r), Run(a.g, b.g, value.g), Run(a.b, b.b, value.b), Run(a.a, b.a, value.a));
}
