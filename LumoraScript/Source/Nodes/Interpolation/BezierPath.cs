// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// Cubic Bezier curves laid end to end from one list: end, control, control, end, control, control, end,
// each curve starting where the one before stopped. position runs from 0 to the number of curves, a whole
// number at each end point, held at both ends. tangent is how fast the point moves as position grows.
// Controls set per end give a sharp corner or a smooth pass as they are placed. Points past the last
// whole curve are left out; a list too short for one curve gives its first point, and an empty one the
// empty value.
[Node("Math/Interpolation/Bezier Path", Description = "Follows connected cubic Bezier curves from a list of endpoints and control points, returning a position and tangent.",
    Keywords = "lerp list, gradient, spline path, path, many points")]
public sealed class BezierPath : ReadNode
{
    public static float Run(ScriptList<float>? points, float position, out float tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    public static double Run(ScriptList<double>? points, float position, out double tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], (double)t, out tangent);
    }

    public static float2 Run(ScriptList<float2>? points, float position, out float2 tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    public static float3 Run(ScriptList<float3>? points, float position, out float3 tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    public static float4 Run(ScriptList<float4>? points, float position, out float4 tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    public static color Run(ScriptList<color>? points, float position, out color tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    public static colorHDR Run(ScriptList<colorHDR>? points, float position, out colorHDR tangent)
    {
        tangent = default;
        if (!Locate(points, position, out int k, out float t, out var only))
            return only;
        var p = points!.Items;
        return BezierCurve.Run(p[k], p[k + 1], p[k + 2], p[k + 3], t, out tangent);
    }

    // The first point of the curve position falls on, or false with the only value there is.
    private static bool Locate<T>(ScriptList<T>? points, float position, out int first, out float t, out T only)
    {
        first = 0;
        t = 0f;
        only = default!;
        int count = points?.Count ?? 0;
        if (count == 0)
            return false;
        int curves = (count - 1) / 3;
        if (curves == 0)
        {
            only = points!.Items[0];
            return false;
        }
        ListSpan.Locate(curves, position, out int curve, out t);
        first = curve * 3;
        return true;
    }
}
