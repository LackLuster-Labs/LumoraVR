// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components;

public interface ICharacterControllerHook : IHook
{
    void AddColliderShape(Collider collider);
    void RemoveColliderShape(Collider collider);
    // Steps character motion once per render frame: re-base body from the
    // root, simulate, write the physics delta back to the root.
    void Simulate(float delta);
    void SetMovementDirection(float3 direction);
    void RequestJump();
    void SetCrouching(bool crouching);
    // Suspend gravity + collision stepping while a non-physical locomotion mode
    // (e.g. noclip) is moving the rig directly. Zeroes velocity when disabled.
    void SetSimulationEnabled(bool enabled);
    void Teleport(float3 position);
    bool IsOnFloor();
    // The body's velocity in world space, metres a second, as the next step starts from it; set, the step
    // carries on from the new one, so gravity, the ground and walking act on it from there.
    float3 Velocity => float3.Zero;
    void SetVelocity(float3 velocity) { }
}
