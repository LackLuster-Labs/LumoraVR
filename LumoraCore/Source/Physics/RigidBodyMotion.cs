// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Physics;

// A simulated rigid body's motion and how hard it is to move, in world space, as one read of the physics engine.
// The inverse inertia is the full world-space tensor as its three columns.
public struct RigidBodyMotion
{
    public float InverseMass;
    public float3 CenterOfMass;
    public float3 LinearVelocity;
    public float3 AngularVelocity;
    public float3 InverseInertia0;
    public float3 InverseInertia1;
    public float3 InverseInertia2;
    public bool Sleeping;

    public float3 ApplyInverseInertia(in float3 v) => InverseInertia0 * v.x + InverseInertia1 * v.y + InverseInertia2 * v.z;

    // The angular impulse that turns the body at v: the inverse inertia undone. Zero if it cannot be.
    public float3 ApplyInertia(in float3 v)
    {
        var a = InverseInertia0;
        var b = InverseInertia1;
        var c = InverseInertia2;
        var bc = float3.Cross(b, c);
        float det = float3.Dot(a, bc);
        if (!(System.MathF.Abs(det) > 1e-20f) || !float.IsFinite(det))
            return float3.Zero;
        var ca = float3.Cross(c, a);
        var ab = float3.Cross(a, b);
        // Rows of the inverse of the matrix whose columns are a, b, c.
        return new float3(float3.Dot(bc, v), float3.Dot(ca, v), float3.Dot(ab, v)) * (1f / det);
    }

    // How fast the body's surface moves at a world point.
    public float3 VelocityAt(in float3 point) => LinearVelocity + float3.Cross(AngularVelocity, point - CenterOfMass);

    // One over the body's effective mass at a world point along a direction: its linear give plus its turning.
    public float InverseMassAt(in float3 point, in float3 direction)
    {
        var arm = float3.Cross(point - CenterOfMass, direction);
        return InverseMass + float3.Dot(arm, ApplyInverseInertia(arm));
    }
}
