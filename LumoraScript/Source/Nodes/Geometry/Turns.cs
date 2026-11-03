// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// What the rotation and geometry nodes share about turning one thing onto another.
internal static class Turns
{
    // A rotation at length one. A zero or broken one is no turn, so it never spreads NaN.
    public static floatQ Unit(floatQ q)
    {
        float length = q.Length;
        if (!(length > 0f) || !float.IsFinite(length))
            return floatQ.Identity;
        return new floatQ(q.x / length, q.y / length, q.z / length, q.w / length);
    }

    // Radians from one rotation to the other along the shorter turn, 0 to pi.
    public static float Between(floatQ a, floatQ b)
        => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(floatQ.Dot(Unit(a), Unit(b)))));

    // The shortest turn that carries the direction from onto the direction to. Opposite directions turn
    // half way round about up made square to them, the way From To Rotation does, so a level direction
    // stays the right way up; along up they turn about a fixed axis square to it. A zero direction is no turn.
    public static floatQ FromTo(float3 from, float3 to)
    {
        from = from.Normalized;
        to = to.Normalized;
        if (from.LengthSquared == 0f || to.LengthSquared == 0f)
            return floatQ.Identity;
        float dot = float3.Dot(from, to);
        if (dot >= 1f - 1e-7f)
            return floatQ.Identity;
        if (dot <= -1f + 1e-7f)
        {
            var level = float3.Up - from * from.y;
            return floatQ.AxisAngle(level.LengthSquared > 1e-6f ? level : Square(from), MathF.PI);
        }
        var axis = float3.Cross(from, to);
        return Unit(new floatQ(axis.x, axis.y, axis.z, 1f + dot));
    }

    // A unit direction square to v, the same one every time for the same v.
    public static float3 Square(float3 v)
    {
        var other = MathF.Abs(v.x) < 0.9f * v.Length ? float3.Right : float3.Up;
        return float3.Cross(v, other).Normalized;
    }
}
