// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core.Physics;

// What a soft body needs from the platform to push back on what it lands on: which solid a resting-contact push-out
// met, how a rigid body this machine simulates is moving and how hard it is to move, and a way to hand it an impulse.
// The world hook implements it next to its other queries; a world without one has soft bodies that only take pushes,
// as before.
public interface IPhysicsContactHook
{
    // The same push-out as IPhysicsQueryHook.ResolveSphere, also naming the slot of the solid it met.
    bool ResolveSphere(in float3 center, float radius, IReadOnlyList<Slot>? exclude, out float3 correctedCenter, out float3 normal, out Slot? met);

    // How a rigid body is moving right now, as the physics engine holds it. False unless this machine simulates it
    // and it is free: held, kinematic, switched off, frozen while it starts, or another machine's, and it does not give.
    bool TryGetMotion(RigidBody body, out RigidBodyMotion motion);

    // A linear impulse (newton seconds) and an angular one (newton metre seconds, about the centre of mass), applied
    // at once. Wakes the body.
    void ApplyImpulse(RigidBody body, in float3 linear, in float3 angular);

    // Puts a body this machine simulates to sleep where it is, still. For a body a soft body is holding up, which the
    // engine cannot see is at rest.
    void Settle(RigidBody body);
}
