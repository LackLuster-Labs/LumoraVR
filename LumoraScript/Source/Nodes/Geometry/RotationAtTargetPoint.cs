// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The turn about pivot that swings the line through point along direction until it passes through
// target: aiming a barrel that sits off a turret's pivot. The line is followed forward to where it is
// as far from the pivot as the target is, and that spot is turned onto the target by the shortest turn.
// A target nearer the pivot than the line ever comes cannot be reached: reaches is false and the
// rotation is no turn. A zero direction is no line and reaches nothing.
[Node("Math/Geometry 3D/Rotation At Target Point", Description = "The turn about pivot that swings the line through point along direction until it passes through target: aiming a barrel that sits off a turret's pivot.")]
public sealed class RotationAtTargetPoint : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run(float3 pivot, float3 point, [Default(0, 0, 1)] float3 direction, float3 target, out bool reaches)
    {
        reaches = false;
        var d = direction.Normalized;
        if (d.LengthSquared == 0f)
            return floatQ.Identity;

        float reach = float3.Distance(pivot, target);
        var nearest = point + d * float3.Dot(pivot - point, d);
        float missSquared = (nearest - pivot).LengthSquared;
        float h2 = reach * reach - missSquared;
        if (!(h2 >= 0f))
            return floatQ.Identity;

        reaches = true;
        var spot = nearest + d * MathF.Sqrt(h2);
        return Turns.FromTo(spot - pivot, target - pivot);
    }
}
