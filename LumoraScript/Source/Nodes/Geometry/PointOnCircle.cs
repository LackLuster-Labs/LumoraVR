// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The point a fraction of a turn round a circle about zero: 0 is +x and a quarter is +y, the way Atan2
// measures angles, so Atan2 of the point over two pi gives the turn back. Whole turns come round again.
[Node("Math/Geometry 2D/Point On Circle", Description = "The point a fraction of a turn round a circle about zero: 0 is +x and a quarter is +y, the way Atan2 measures angles, so Atan2 of the point over two pi gives the turn back.")]
public sealed class PointOnCircle : ValueNode
{
    [return: Port("point")]
    public static float2 Run(float turn, float radius = 1f)
    {
        float angle = (turn - MathF.Floor(turn)) * (2f * MathF.PI);
        return new float2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }
}
