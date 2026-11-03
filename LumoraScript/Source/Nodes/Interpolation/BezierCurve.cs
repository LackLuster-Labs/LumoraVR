// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// A cubic Bezier from from to to, pulled toward the two control points between: 0 is from, 1 is to, and
// t past those carries the curve on. tangent is how fast the point moves as t grows, pointing the way the
// curve heads. Vectors and colors go lane by lane. Bezier Path runs one through a list of points.
[Node("Math/Interpolation/Bezier Curve", Description = "Finds a position and tangent along a cubic Bezier curve defined by two endpoints and two control points.")]
public sealed class BezierCurve : ValueNode
{
    public static float Run(float from, float fromControl, float toControl, float to, float t, out float tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }

    public static double Run(double from, double fromControl, double toControl, double to, double t, out double tangent)
    {
        var (w0, w1, w2, w3) = CurveWeights.Bezier(t);
        var (s0, s1, s2, s3) = CurveWeights.BezierSlope(t);
        tangent = from * s0 + fromControl * s1 + toControl * s2 + to * s3;
        return from * w0 + fromControl * w1 + toControl * w2 + to * w3;
    }

    public static float2 Run(float2 from, float2 fromControl, float2 toControl, float2 to, float t, out float2 tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }

    public static float3 Run(float3 from, float3 fromControl, float3 toControl, float3 to, float t, out float3 tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }

    public static float4 Run(float4 from, float4 fromControl, float4 toControl, float4 to, float t, out float4 tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }

    public static color Run(color from, color fromControl, color toControl, color to, float t, out color tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }

    public static colorHDR Run(colorHDR from, colorHDR fromControl, colorHDR toControl, colorHDR to, float t, out colorHDR tangent)
    {
        var w = CurveWeights.Bezier(t);
        var s = CurveWeights.BezierSlope(t);
        tangent = from * s.x + fromControl * s.y + toControl * s.z + to * s.w;
        return from * w.x + fromControl * w.y + toControl * w.z + to * w.w;
    }
}
