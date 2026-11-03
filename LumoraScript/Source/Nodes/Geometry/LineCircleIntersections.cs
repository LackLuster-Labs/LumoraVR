// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where the endless line through a and b crosses a circle: 0, 1 where it only touches, or 2, first the
// one met first going from a toward b. A crossing that is not there reads zero, and a touch gives the
// same point twice. a and b at one point make no line and cross nothing.
[Node("Math/Geometry 2D/Line Circle Intersections", Description = "Where the endless line through a and b crosses a circle: 0, 1 where it only touches, or 2, first the one met first going from a toward b.")]
public sealed class LineCircleIntersections : ValueNode
{
    // Within a millionth of the radius squared, a near miss or a near double crossing is a touch.
    private const float Touch = 1e-6f;

    [return: Port("count")]
    public static int Run(float2 center, [Default(1)] float radius, float2 a, float2 b, out float2 first, out float2 second)
    {
        first = float2.Zero;
        second = float2.Zero;
        var along = b - a;
        float lengthSquared = float2.Dot(along, along);
        if (!(lengthSquared > 0f))
            return 0;

        float nearestAt = float2.Dot(center - a, along) / lengthSquared;
        var nearest = a + along * nearestAt;
        var off = nearest - center;
        float r2 = radius * radius;
        float h2 = r2 - float2.Dot(off, off);
        if (!(h2 >= -r2 * Touch))
            return 0;
        if (h2 <= r2 * Touch)
        {
            first = nearest;
            second = nearest;
            return 1;
        }

        float step = MathF.Sqrt(h2 / lengthSquared);
        first = a + along * (nearestAt - step);
        second = a + along * (nearestAt + step);
        return 2;
    }
}
