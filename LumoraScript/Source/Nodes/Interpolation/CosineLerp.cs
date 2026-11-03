// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// From a to b along half a cosine wave: slow away from a, fastest half way, slow into b. t is held to 0
// to 1, since past the ends the wave would turn back. Vectors and colors go lane by lane.
[Node("Math/Interpolation/Cosine Lerp", Description = "From a to b along half a cosine wave: slow away from a, fastest half way, slow into b. t is held to 0 to 1, since past the ends the wave would turn back.")]
public sealed class CosineLerp : ValueNode
{
    public static float Run(float a, float b, float t) => a + (b - a) * CurveWeights.Cosine(t);
    public static double Run(double a, double b, double t) => a + (b - a) * CurveWeights.Cosine(t);
    public static float2 Run(float2 a, float2 b, float t) => a + (b - a) * CurveWeights.Cosine(t);
    public static float3 Run(float3 a, float3 b, float t) => a + (b - a) * CurveWeights.Cosine(t);
    public static float4 Run(float4 a, float4 b, float t) => a + (b - a) * CurveWeights.Cosine(t);
    public static color Run(color a, color b, float t) => a + (b - a) * CurveWeights.Cosine(t);
    public static colorHDR Run(colorHDR a, colorHDR b, float t) => a + (b - a) * CurveWeights.Cosine(t);
}
