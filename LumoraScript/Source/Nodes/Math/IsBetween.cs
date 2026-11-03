// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Numerics;
using Lumora.Core.Math;

namespace Lumora.Script;

// Whether value lies between min and max, both ends included. Bounds given the wrong way round are taken the
// right way round rather than holding nothing. NaN is between nothing, and nothing is between a NaN bound.
// Vectors and colors need every lane between the same lanes of min and max. Dates compare by ticks alone,
// whatever kind they are. Unwired, the bounds are 0 and 1, in every lane of a vector or a color.
[Node("Logic/Comparison/Is Between", Description = "Whether value lies between min and max, both ends included.",
    Keywords = "in range, within, inside, range check")]
public sealed class IsBetween : ValueNode
{
    public static bool Run(float value, float min = 0f, float max = 1f) => Within(value, min, max);
    public static bool Run(double value, double min = 0d, double max = 1d) => Within(value, min, max);
    public static bool Run(int value, int min = 0, int max = 1) => Within(value, min, max);
    public static bool Run(long value, long min = 0L, long max = 1L) => Within(value, min, max);
    public static bool Run(uint value, uint min = 0u, uint max = 1u) => Within(value, min, max);
    public static bool Run(ulong value, ulong min = 0ul, ulong max = 1ul) => Within(value, min, max);
    public static bool Run(float2 value, float2 min, [Default(1)] float2 max) => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y);

    public static bool Run(float3 value, float3 min, [Default(1)] float3 max)
        => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y) && Run(value.z, min.z, max.z);

    public static bool Run(float4 value, float4 min, [Default(1)] float4 max)
        => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y) && Run(value.z, min.z, max.z) && Run(value.w, min.w, max.w);

    public static bool Run(int2 value, int2 min, [Default(1)] int2 max) => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y);

    public static bool Run(int3 value, int3 min, [Default(1)] int3 max)
        => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y) && Run(value.z, min.z, max.z);

    public static bool Run(int4 value, int4 min, [Default(1)] int4 max)
        => Run(value.x, min.x, max.x) && Run(value.y, min.y, max.y) && Run(value.z, min.z, max.z) && Run(value.w, min.w, max.w);

    public static bool Run(color value, color min, [Default(1)] color max)
        => Run(value.r, min.r, max.r) && Run(value.g, min.g, max.g) && Run(value.b, min.b, max.b) && Run(value.a, min.a, max.a);

    public static bool Run(colorHDR value, colorHDR min, [Default(1)] colorHDR max)
        => Run(value.r, min.r, max.r) && Run(value.g, min.g, max.g) && Run(value.b, min.b, max.b) && Run(value.a, min.a, max.a);

    public static bool Run(DateTime value, DateTime min, DateTime max) => Within(value.Ticks, min.Ticks, max.Ticks);
    public static bool Run(TimeSpan value, TimeSpan min, TimeSpan max) => Within(value.Ticks, min.Ticks, max.Ticks);

    private static bool Within<T>(T value, T min, T max) where T : INumber<T>
        => min <= max ? value >= min && value <= max : value >= max && value <= min;
}
