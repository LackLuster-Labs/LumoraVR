// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// A min above max gives min, where Math.Clamp would throw. Vectors and colors go lane by lane. min and max
// default to 0 and 1, in every lane of a vector or a color.
[Node("Math/Clamp", Description = "Keeps a value within the given minimum and maximum.",
    Keywords = "limit, restrict, saturate")]
public sealed class Clamp : ValueNode
{
    public static int Run(int value, int min = 0, int max = 1) => Math.Max(min, Math.Min(max, value));
    public static long Run(long value, long min = 0L, long max = 1L) => Math.Max(min, Math.Min(max, value));
    public static float Run(float value, float min = 0f, float max = 1f) => MathF.Max(min, MathF.Min(max, value));
    public static double Run(double value, double min = 0d, double max = 1d) => Math.Max(min, Math.Min(max, value));

    public static float2 Run(float2 value, float2 min, [Default(1)] float2 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y));

    public static float3 Run(float3 value, float3 min, [Default(1)] float3 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y), Run(value.z, min.z, max.z));

    public static float4 Run(float4 value, float4 min, [Default(1)] float4 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y), Run(value.z, min.z, max.z), Run(value.w, min.w, max.w));

    public static int2 Run(int2 value, int2 min, [Default(1)] int2 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y));

    public static int3 Run(int3 value, int3 min, [Default(1)] int3 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y), Run(value.z, min.z, max.z));

    public static int4 Run(int4 value, int4 min, [Default(1)] int4 max)
        => new(Run(value.x, min.x, max.x), Run(value.y, min.y, max.y), Run(value.z, min.z, max.z), Run(value.w, min.w, max.w));

    public static color Run(color value, color min, [Default(1)] color max)
        => new(Run(value.r, min.r, max.r), Run(value.g, min.g, max.g), Run(value.b, min.b, max.b), Run(value.a, min.a, max.a));

    public static colorHDR Run(colorHDR value, colorHDR min, [Default(1)] colorHDR max)
        => new(Run(value.r, min.r, max.r), Run(value.g, min.g, max.g), Run(value.b, min.b, max.b), Run(value.a, min.a, max.a));

    public static ScriptBigInt Run(ScriptBigInt? value, ScriptBigInt? min, ScriptBigInt? max) => ScriptBigInt.Max(min, ScriptBigInt.Min(max, value));

    public static uint Run(uint value, uint min = 0u, uint max = 1u) => Math.Max(min, Math.Min(max, value));
    public static ulong Run(ulong value, ulong min = 0ul, ulong max = 1ul) => Math.Max(min, Math.Min(max, value));
}
