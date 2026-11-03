// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// How far a steady spin turns in seconds. The angular velocity points along the axis it spins about, the
// way the right hand's fingers curl round the thumb, and its length is the speed in degrees a second. No
// spin, or no time, turns nothing; a time below 0 turns back.
[Node("Math/Rotations/Angular Velocity Turn", Description = "How far a steady spin turns in seconds.",
    Keywords = "delta rotation, spin, rotation per second")]
public sealed class AngularVelocityTurn : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run(float3 angularVelocity, float seconds)
    {
        double x = angularVelocity.x, y = angularVelocity.y, z = angularVelocity.z;
        double speed = Math.Sqrt(x * x + y * y + z * z);
        return RotationMath.FromAxis(x, y, z, speed * seconds * (Math.PI / 180d));
    }
}
