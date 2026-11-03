// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The point of the segment from a to b nearest to point, and how far along it that is: 0 at a, 1 at b.
// A segment with no length is the point a. For the nearest point of an endless line, Sphere For Tangent
// Line gives it as the tangent point.
[Node("Math/Geometry 3D/Closest Point On Segment", Description = "The point of the segment from a to b nearest to point, and how far along it that is: 0 at a, 1 at b.",
    Keywords = "nearest point, project point, point to line, line")]
public sealed class ClosestPointOnSegment : ValueNode
{
    [return: Port("closest")]
    public static float3 Run(float3 a, float3 b, float3 point, out float t)
    {
        var along = b - a;
        float lengthSquared = float3.Dot(along, along);
        t = lengthSquared > 0f ? Math.Clamp(float3.Dot(point - a, along) / lengthSquared, 0f, 1f) : 0f;
        if (float.IsNaN(t))
            t = 0f;
        return a + along * t;
    }

    [return: Port("closest")]
    public static float2 Run(float2 a, float2 b, float2 point, out float t)
    {
        var along = b - a;
        float lengthSquared = float2.Dot(along, along);
        t = lengthSquared > 0f ? Math.Clamp(float2.Dot(point - a, along) / lengthSquared, 0f, 1f) : 0f;
        if (float.IsNaN(t))
            t = 0f;
        return a + along * t;
    }
}
