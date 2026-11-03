// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// The axis a rotation turns about, at length one, and how far in degrees, 0 to 180: the shorter way round, so
// a turn of 270 comes back as 90 about the opposite axis. No turn at all has no axis of its own and gives +z,
// forward, with 0 degrees. Axis Angle turns them back into the rotation.
[Node("Math/Rotations/To Axis Angle", Description = "The axis a rotation turns about, at length one, and how far in degrees, 0 to 180: the shorter way round, so a turn of 270 comes back as 90 about the opposite axis.")]
public sealed class ToAxisAngle : ValueNode
{
    public static void Run([Default(0, 0, 0, 1)] floatQ rotation, out float3 axis, out float degrees)
    {
        RotationMath.ToAxis(rotation, out double x, out double y, out double z, out double radians);
        axis = new float3((float)x, (float)y, (float)z);
        degrees = (float)(radians * (180d / Math.PI));
    }
}
