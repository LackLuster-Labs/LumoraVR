// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// physics behavior delegated to ICharacterControllerHook (e.g. CharacterControllerHook in LumoraGodot)
// EARLY update order: the character steps (and moves the user root) BEFORE the component pass, right
// after device tracking lands. Everything that bakes a WORLD-space value during its update - the laser
// hold point, laser cursor, IK solves, nameplate followers - then computes against the post-move root.
// This used to be +1000000 (step last, consume locomotion input same-frame), which made every one of
// those bakes one frame of root motion behind the camera: the whole world felt rough while moving, worst
// while holding something. Locomotion input written at order 0 is now consumed NEXT frame - one frame of
// input latency on walk start beats one frame of visual lag on everything you carry. Must stay after
// TrackedDevicePositioner (-1000000): the rebase reads the head slot's fresh tracking pose. - xlinka
[ComponentCategory("Physics")]
[DefaultUpdateOrder(-500000)]
public class CharacterController : ImplementableComponent, IColliderOwner
{
    // When set, the character body follows this slot's ground projection
    // (room-scale head) instead of the root origin, and physics writes back
    // only the delta it produced. Walking into a wall physically pushes the
    // root back; turning around the head never disturbs the body.
    public readonly SyncRef<Slot> HeadReference = new();

    // MOVEMENT PARAMETERS

    public float Speed { get; set; } = 5.0f;
    public float AirSpeed { get; set; } = 1.0f;
    public float JumpSpeed { get; set; } = 6.0f;
    public float CrouchSpeed { get; set; } = 2.5f;
    public float SprintMultiplier { get; set; } = 1.7f;
    public float StandingHeight { get; set; } = 1.8f;
    public float CrouchHeight { get; set; } = 1.4f;
    public float CrouchTransitionSpeed { get; set; } = 8.0f;

    // No gravity and no ground: the move direction is followed in all three axes at AirSpeed, and walls still
    // stop the body. A flying locomotion turns it on while it is the one in use.
    public bool Flying { get; set; }

    // STATE

    private readonly List<Collider> _colliders = new List<Collider>();
    private bool _isReady = false;
    private bool _isCrouching = false;
    private bool _isSprinting = false;
    private float3 _velocity = float3.Zero;

    private UserRoot _userRoot = null!;

    public enum MovementState { Grounded, InAir }
    private MovementState _currentState = MovementState.InAir;

    private ICharacterControllerHook CharacterHook => (Hook as ICharacterControllerHook) ?? null!;

    // IColliderOwner IMPLEMENTATION

    public bool Kinematic => false;

    public void OnColliderAdded(Collider collider)
    {
        if (!_colliders.Contains(collider))
        {
            _colliders.Add(collider);
            // After setup the shapes are already built; a collider arriving later needs its own.
            if (_isReady)
                CharacterHook?.AddColliderShape(collider);
            LumoraLogger.Debug($"CharacterController: Added collider {collider.GetType().Name}");
        }
    }

    public void OnColliderRemoved(Collider collider)
    {
        if (_colliders.Remove(collider))
        {
            CharacterHook?.RemoveColliderShape(collider);
            LumoraLogger.Debug($"CharacterController: Removed collider {collider.GetType().Name}");
        }
    }

    // An avatar fit or an inspector edit of the capsule. The hook refits its shapes on the step after its
    // ApplyChanges, so the body the world pushes is the one the collider describes.
    public void OnColliderShapeChanged(Collider collider)
    {
        if (_colliders.Contains(collider))
            RunApplyChanges();
    }

    public void PostprocessBoundsOffset(ref float3 offset) { }

    // INITIALIZATION

    public override void OnAwake()
    {
        base.OnAwake();
        _userRoot = Slot.GetComponent<UserRoot>();
        if (_userRoot == null)
            LumoraLogger.Warn("CharacterController: No UserRoot found!");
    }

    public override void OnStart()
    {
        base.OnStart();
        TryInitializeLocalUser();
    }

    private void DiscoverColliders()
    {
        _colliders.Clear();
        foreach (var component in Slot.Components)
        {
            if (component is Collider collider &&
                collider.Enabled &&
                collider.Type.Value == ColliderType.CharacterController)
            {
                _colliders.Add(collider);
                LumoraLogger.Debug($"CharacterController: Found {collider.GetType().Name} with Type=CharacterController");
            }
        }

        if (_colliders.Count > 1)
            LumoraLogger.Warn($"CharacterController: Found {_colliders.Count} colliders, using first one");

        LumoraLogger.Debug($"CharacterController: Discovered {_colliders.Count} colliders in same slot");
    }

    public IReadOnlyList<Collider> GetColliders() => _colliders;

    // UPDATE

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Stop the cached walking velocity and gravity step from moving a parked avatar.
        if (World?.IsFocused != true)
            return;

        if (!_isReady)
            TryInitializeLocalUser();

        if (!(_userRoot?.IsLocalUserRoot ?? false))
            return;

        // Step once per render frame (not on a fixed tick): a 60Hz root on a
        // 90-144Hz headset reads as stutter, and two unsynchronized clocks
        // beat against each other. Camera output runs after the engine update
        // in the same frame, so the step result is visible immediately.
        if (_isReady && CharacterHook != null)
            CharacterHook.Simulate(delta);
    }

    private void TryInitializeLocalUser()
    {
        if (_isReady)
            return;

        if (_userRoot == null)
        {
            _userRoot = Slot.GetComponent<UserRoot>();
            if (_userRoot == null)
                return;
        }

        if (!_userRoot.IsLocalUserRoot)
            return;

        DiscoverColliders();

        if (CharacterHook != null)
        {
            foreach (var collider in _colliders)
                CharacterHook.AddColliderShape(collider);

            RunApplyChanges();
            LumoraLogger.Debug("CharacterController: Physics enabled!");
        }
        else
        {
            LumoraLogger.Warn("CharacterController: CharacterHook is null!");
        }

        _isReady = true;
        LumoraLogger.Debug($"CharacterController: Initialized for local user '{_userRoot.ActiveUser.UserName.Value}' with {_colliders.Count} colliders");
    }

    // PUBLIC API (Called by LocomotionController)

    public bool IsReady => _isReady;

    public void SetMovementDirection(float3 direction)
    {
        _velocity = new float3(direction.x, _velocity.y, direction.z);
        CharacterHook?.SetMovementDirection(direction);
    }

    // Suspend gravity/collision stepping so a module like noclip can move the
    // rig directly without the controller fighting it back down.
    public void SetSimulationEnabled(bool enabled)
    {
        CharacterHook?.SetSimulationEnabled(enabled);
    }

    public void RequestJump()
    {
        CharacterHook?.RequestJump();
    }

    public void SetCrouching(bool crouching)
    {
        _isCrouching = crouching;
        if (crouching)
            _isSprinting = false;
        CharacterHook?.SetCrouching(crouching);
    }

    public void SetSprinting(bool sprinting)
    {
        if (_isCrouching)
        {
            _isSprinting = false;
            return;
        }
        _isSprinting = sprinting;
    }

    public bool IsCrouching => _isCrouching;
    public bool IsSprinting => _isSprinting;

    public float GetCurrentSpeed()
    {
        if (_isCrouching) return CrouchSpeed;
        if (_currentState == MovementState.InAir) return AirSpeed;
        return _isSprinting ? Speed * SprintMultiplier : Speed;
    }

    public void Teleport(float3 position)
    {
        if (CharacterHook != null)
            CharacterHook.Teleport(position);
        else
            Slot.GlobalPosition = position;
    }

    public bool IsGrounded()
    {
        if (CharacterHook != null)
            return CharacterHook.IsOnFloor();
        return _currentState == MovementState.Grounded;
    }

    public float3 GetVelocity() => _velocity;

    // How fast the body itself moves, metres a second in world space, as the physics step has it; setting it is
    // a push the step carries on from. Only the local user's character steps.
    public float3 BodyVelocity => CharacterHook?.Velocity ?? float3.Zero;

    public void SetBodyVelocity(float3 velocity) => CharacterHook?.SetVelocity(velocity);

    // CLEANUP

    public override void OnDestroy()
    {
        _colliders.Clear();
        base.OnDestroy();
        LumoraLogger.Debug("CharacterController: Destroyed");
    }
}
