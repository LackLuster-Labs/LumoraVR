// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// 1, -1 or 0. NaN gives 0. Vectors and colors go lane by lane.
[Node("Math/Sign", Description = "Returns -1 for a negative value, 0 for zero, or 1 for a positive value.",
    Keywords = "positive, negative, direction")]
public sealed class Sign : ValueNode
{
    public static int Run(int value) => value > 0 ? 1 : value < 0 ? -1 : 0;
    public static float Run(float value) => value > 0f ? 1f : value < 0f ? -1f : 0f;
    public static double Run(double value) => value > 0d ? 1d : value < 0d ? -1d : 0d;
    public static long Run(long value) => value > 0L ? 1L : value < 0L ? -1L : 0L;
    public static float2 Run(float2 value) => new(Run(value.x), Run(value.y));
    public static float3 Run(float3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    public static float4 Run(float4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));
    public static int2 Run(int2 value) => new(Run(value.x), Run(value.y));
    public static int3 Run(int3 value) => new(Run(value.x), Run(value.y), Run(value.z));
    public static int4 Run(int4 value) => new(Run(value.x), Run(value.y), Run(value.z), Run(value.w));
    public static color Run(color value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));
    public static colorHDR Run(colorHDR value) => new(Run(value.r), Run(value.g), Run(value.b), Run(value.a));

    public static int Run(ScriptBigInt? value) => ScriptBigInt.Sign(value);
}
