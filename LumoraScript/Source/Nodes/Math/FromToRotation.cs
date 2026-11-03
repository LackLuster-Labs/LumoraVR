// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The shortest turn that points from along to. Lengths do not matter, and a zero vector gives no turn.
// Opposite directions have many shortest turns; this is the half turn about up made square to from, so a
// level direction turns round about up and stays the right way up, and from along up turns about the axis
// square to it and +x. Between two rotations it is the turn that takes from to to: Combine from with it
// and you get to. A zero rotation counts as no turn.
[Node("Math/Rotations/From To Rotation", Description = "The shortest turn that points from along to.")]
public sealed class FromToRotation : ValueNode
{
    // Past this the directions count as opposite, and the cross product is too small to trust as an axis.
    private const double Opposite = 1e-12d;

    [return: Port("rotation")]
    public static floatQ Run(float3 from, float3 to)
    {
        double fl = Math.Sqrt((double)from.x * from.x + (double)from.y * from.y + (double)from.z * from.z);
        double tl = Math.Sqrt((double)to.x * to.x + (double)to.y * to.y + (double)to.z * to.z);
        if (!(fl > 0d) || !(tl > 0d) || double.IsInfinity(fl) || double.IsInfinity(tl))
            return floatQ.Identity;
        double ax = from.x / fl, ay = from.y / fl, az = from.z / fl;
        double bx = to.x / tl, by = to.y / tl, bz = to.z / tl;
        double w = 1d + ax * bx + ay * by + az * bz;
        if (w < Opposite)
        {
            // Up with its share along from taken out, or the axis square to from and +x when from lies along up.
            double px = -ax * ay, py = 1d - ay * ay, pz = -az * ay;
            if (px * px + py * py + pz * pz < 1e-6d)
                (px, py, pz) = (0d, az, -ay);
            return RotationMath.FromAxis(px, py, pz, Math.PI);
        }
        double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
        double length = Math.Sqrt(cx * cx + cy * cy + cz * cz + w * w);
        return new floatQ((float)(cx / length), (float)(cy / length), (float)(cz / length), (float)(w / length));
    }

    [return: Port("rotation")]
    public static floatQ Run([Default(0, 0, 0, 1)] floatQ from, [Default(0, 0, 0, 1)] floatQ to) => Turns.Unit(to) * Turns.Unit(from).Conjugate;
}
