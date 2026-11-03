// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Script;

// The nearest pair of points on two endless lines, each a point on it and a direction, and how far apart
// they are. Parallel lines have no one nearest pair: parallel says so, and the pair is pointA with the
// point across from it on line B. A zero direction makes that line its point.
[Node("Math/Geometry 3D/Closest Points Between Lines", Description = "The nearest pair of points on two endless lines, each a point on it and a direction, and how far apart they are.")]
public sealed class ClosestPointsBetweenLines : ValueNode
{
    // Lines closer to parallel than this, as the squared sine of the angle between them, are parallel:
    // below it float rounding decides where the pair lands.
    private const float Parallel = 1e-7f;

    public static void Run(float3 pointA, float3 directionA, float3 pointB, float3 directionB, out float3 onA, out float3 onB,
        out float distance, out bool parallel)
    {
        var da = directionA.Normalized;
        var db = directionB.Normalized;
        bool lineA = da.LengthSquared > 0f, lineB = db.LengthSquared > 0f;
        var apart = pointA - pointB;
        float s = 0f, t = 0f;
        parallel = false;

        if (lineA && lineB)
        {
            float cos = float3.Dot(da, db);
            float sin2 = 1f - cos * cos;
            float c = float3.Dot(da, apart), f = float3.Dot(db, apart);
            if (sin2 > Parallel)
            {
                s = (cos * f - c) / sin2;
                t = (f - cos * c) / sin2;
            }
            else
            {
                parallel = true;
                t = f;
            }
        }
        else if (lineB)
        {
            t = float3.Dot(db, apart);
        }
        else if (lineA)
        {
            s = -float3.Dot(da, apart);
        }

        onA = pointA + da * s;
        onB = pointB + db * t;
        distance = float3.Distance(onA, onB);
    }
}
