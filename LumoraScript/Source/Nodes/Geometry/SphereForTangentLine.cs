// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The sphere about center that just touches the endless line through linePoint along lineDirection:
// the tangent point where it touches, which is the line's nearest point to center, and its radius, the
// line's distance from center. A zero direction makes the line its point.
[Node("Math/Geometry 3D/Sphere For Tangent Line", Description = "Finds the sphere around a centre that just touches a line, returning its radius and the touching point.")]
public sealed class SphereForTangentLine : ValueNode
{
    public static void Run(float3 linePoint, float3 lineDirection, float3 center, out float3 tangentPoint, out float radius)
    {
        var d = lineDirection.Normalized;
        tangentPoint = linePoint + d * float3.Dot(center - linePoint, d);
        radius = float3.Distance(center, tangentPoint);
    }
}
