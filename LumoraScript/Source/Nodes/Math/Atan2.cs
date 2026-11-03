// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The angle of the point (x, y) from the +x axis, in radians, -pi to pi. Vectors and colors go lane by
// lane: each lane of y with the same lane of x.
[Node("Math/Trigonometry/Atan2", Description = "The angle of the point (x, y) from the +x axis, in radians, -pi to pi.",
    Keywords = "arctangent, atan, angle of vector, heading")]
public sealed class Atan2 : ValueNode
{
    [return: Port("radians")] public static float Run(float y, float x) => MathF.Atan2(y, x);
    [return: Port("radians")] public static double Run(double y, double x) => Math.Atan2(y, x);
    [return: Port("radians")] public static float2 Run(float2 y, float2 x) => new(Run(y.x, x.x), Run(y.y, x.y));
    [return: Port("radians")] public static float3 Run(float3 y, float3 x) => new(Run(y.x, x.x), Run(y.y, x.y), Run(y.z, x.z));
    [return: Port("radians")] public static float4 Run(float4 y, float4 x) => new(Run(y.x, x.x), Run(y.y, x.y), Run(y.z, x.z), Run(y.w, x.w));
    [return: Port("radians")] public static color Run(color y, color x) => new(Run(y.r, x.r), Run(y.g, x.g), Run(y.b, x.b), Run(y.a, x.a));
    [return: Port("radians")] public static colorHDR Run(colorHDR y, colorHDR x) => new(Run(y.r, x.r), Run(y.g, x.g), Run(y.b, x.b), Run(y.a, x.a));

    // A single number on either side goes with every lane of the other.
    [return: Port("radians")] public static float2 Run(float2 y, float x) => new(Run(y.x, x), Run(y.y, x));
    [return: Port("radians")] public static float3 Run(float3 y, float x) => new(Run(y.x, x), Run(y.y, x), Run(y.z, x));
    [return: Port("radians")] public static float4 Run(float4 y, float x) => new(Run(y.x, x), Run(y.y, x), Run(y.z, x), Run(y.w, x));
    [return: Port("radians")] public static color Run(color y, float x) => new(Run(y.r, x), Run(y.g, x), Run(y.b, x), Run(y.a, x));
    [return: Port("radians")] public static colorHDR Run(colorHDR y, float x) => new(Run(y.r, x), Run(y.g, x), Run(y.b, x), Run(y.a, x));
    [return: Port("radians")] public static float2 Run(float y, float2 x) => new(Run(y, x.x), Run(y, x.y));
    [return: Port("radians")] public static float3 Run(float y, float3 x) => new(Run(y, x.x), Run(y, x.y), Run(y, x.z));
    [return: Port("radians")] public static float4 Run(float y, float4 x) => new(Run(y, x.x), Run(y, x.y), Run(y, x.z), Run(y, x.w));
    [return: Port("radians")] public static color Run(float y, color x) => new(Run(y, x.r), Run(y, x.g), Run(y, x.b), Run(y, x.a));
    [return: Port("radians")] public static colorHDR Run(float y, colorHDR x) => new(Run(y, x.r), Run(y, x.g), Run(y, x.b), Run(y, x.a));
}
