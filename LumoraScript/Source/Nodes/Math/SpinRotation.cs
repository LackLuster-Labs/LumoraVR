// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Script;

// Where a spin has turned to after seconds while drag slows it, and the spin left by then. The angular
// velocity points along the axis it spins about, its length the speed in degrees a second, the way Angular
// Velocity Turn reads it. Drag slows it in proportion to its speed: at a drag of 1 a second leaves e^-1,
// about 37%, of it, so the spin fades rather than stops. No drag spins on steadily. The turn is taken about
// the one axis, not read as angles about x, y and z.
[Node("Math/Physics/Spin Rotation", Description = "Where a spin has turned to after seconds while drag slows it, and the spin left by then.",
    Keywords = "torque, angular drag, spin down")]
public sealed class SpinRotation : ValueNode
{
    [return: Port("rotation")]
    public static floatQ Run(float3 angularVelocity, float drag, float seconds, out float3 spin)
    {
        double u = (double)drag * seconds;
        double fade = Math.Exp(-u);
        spin = new float3((float)(angularVelocity.x * fade), (float)(angularVelocity.y * fade), (float)(angularVelocity.z * fade));

        double x = angularVelocity.x, y = angularVelocity.y, z = angularVelocity.z;
        double degrees = Math.Sqrt(x * x + y * y + z * z) * seconds * DragMath.Coast(u);
        return RotationMath.FromAxis(x, y, z, degrees * (Math.PI / 180d));
    }
}
