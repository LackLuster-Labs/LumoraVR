// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Halfway between a and b. Each is halved before they are added, so two huge numbers do not overflow on the
// way. Vectors and colors go lane by lane.
[Node("Math/Average", Description = "Halfway between a and b.",
    Keywords = "mean, midpoint, halfway")]
public sealed class Average : ValueNode
{
    public static float Run(float a, float b) => a * 0.5f + b * 0.5f;
    public static double Run(double a, double b) => a * 0.5d + b * 0.5d;
    public static float2 Run(float2 a, float2 b) => a * 0.5f + b * 0.5f;
    public static float3 Run(float3 a, float3 b) => a * 0.5f + b * 0.5f;
    public static float4 Run(float4 a, float4 b) => a * 0.5f + b * 0.5f;
    public static color Run(color a, color b) => a * 0.5f + b * 0.5f;
    public static colorHDR Run(colorHDR a, colorHDR b) => a * 0.5f + b * 0.5f;
}
