// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where something thrown at launchVelocity has got to after seconds, as an offset from where it left, and
// its velocity then. Gravity pulls it the whole way, in metres a second squared, (0, -9.81, 0) near the
// ground and when unwired. Drag slows it in proportion to its speed, the way Spin Rotation's does, so a fall tends to gravity
// over drag and never passes it. No drag is the plain arc. Nothing it could hit is taken into account.
[Node("Math/Physics/Trajectory Position", Description = "Where something thrown at launchVelocity has got to after seconds, as an offset from where it left, and its velocity then.",
    Keywords = "projectile, ballistic, throw, arc, gravity, launch")]
public sealed class TrajectoryPosition : ValueNode
{
    [return: Port("offset")]
    public static float3 Run(float3 launchVelocity, [Default(0, -9.81, 0)] float3 gravity, float drag, float seconds, out float3 velocity)
    {
        double t = seconds;
        double u = (double)drag * t;
        double coast = t * DragMath.Coast(u);
        double fall = t * t * DragMath.Fall(u);
        double fade = Math.Exp(-u);
        velocity = new float3((float)(launchVelocity.x * fade + gravity.x * coast), (float)(launchVelocity.y * fade + gravity.y * coast),
            (float)(launchVelocity.z * fade + gravity.z * coast));
        return new float3((float)(launchVelocity.x * coast + gravity.x * fall), (float)(launchVelocity.y * coast + gravity.y * fall),
            (float)(launchVelocity.z * coast + gravity.z * fall));
    }
}
