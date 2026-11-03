// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A cubic Hermite blend: from at 0 leaving with fromTangent, to at 1 arriving with toTangent, t past
// those carrying the curve on. tangent is how fast the value moves as t grows. Each value's tangent is
// an input of its own, and Cubic Lerp Along takes a list of tangents beside the list of values, so no
// packed value and tangent type is needed. Vectors and colors go lane by lane. -xlinka
[Node("Math/Interpolation/Cubic Lerp", Description = "A cubic Hermite blend: from at 0 leaving with fromTangent, to at 1 arriving with toTangent, t past those carrying the curve on. tangent is how fast the value moves as t grows.",
    Keywords = "hermite, spline, tangent lerp")]
public sealed class CubicLerp : ValueNode
{
    public static float Run(float from, float fromTangent, float to, float toTangent, float t, out float tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }

    public static double Run(double from, double fromTangent, double to, double toTangent, double t, out double tangent)
    {
        var (w0, w1, w2, w3) = CurveWeights.Hermite(t);
        var (s0, s1, s2, s3) = CurveWeights.HermiteSlope(t);
        tangent = from * s0 + fromTangent * s1 + to * s2 + toTangent * s3;
        return from * w0 + fromTangent * w1 + to * w2 + toTangent * w3;
    }

    public static float2 Run(float2 from, float2 fromTangent, float2 to, float2 toTangent, float t, out float2 tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }

    public static float3 Run(float3 from, float3 fromTangent, float3 to, float3 toTangent, float t, out float3 tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }

    public static float4 Run(float4 from, float4 fromTangent, float4 to, float4 toTangent, float t, out float4 tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }

    public static color Run(color from, color fromTangent, color to, color toTangent, float t, out color tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }

    public static colorHDR Run(colorHDR from, colorHDR fromTangent, colorHDR to, colorHDR toTangent, float t, out colorHDR tangent)
    {
        var w = CurveWeights.Hermite(t);
        var s = CurveWeights.HermiteSlope(t);
        tangent = from * s.x + fromTangent * s.y + to * s.z + toTangent * s.w;
        return from * w.x + fromTangent * w.y + to * w.z + toTangent * w.w;
    }
}
