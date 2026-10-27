// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// One module for stick AND keyboard. Reads movement / turn / jump through
// LocomotionInputHelper which collapses the input source decision behind a
// uniform interface, then drives the CharacterController. Snap turn comes
// from TurnSubmodule (active only when a stick is present, desktop just gets
// nothing on that axis because mouse-look already handles yaw).
// - xlinka
[ComponentCategory("Users/Locomotion")]
public class PhysicalLocomotion : SmoothLocomotionBase
{
    protected override string StockName => "Walk";

    // An installed walk's own pace, metres a second; zero keeps the user's own. Put on while it is active and
    // taken off again when another locomotion takes over.
    public readonly Sync<float> Speed = new();
    public readonly Sync<float> AirSpeed = new();
    public readonly Sync<float> JumpSpeed = new();
    private (float Speed, float Air, float Jump)? _ownPace;

    // Flies instead of walking: no gravity, the stick goes where the head looks and the fly axis straight up or
    // down, and walls still stop the body. Paced by AirSpeed.
    public readonly Sync<bool> Fly = new();

    // A grip with nothing in the hand, close enough to something solid, takes hold of it: the person hangs there
    // and pulls themselves along by moving the hand, and lets go with the speed the pull had, times FlingScale.
    // GripRadius is how close the hand must be, in metres at the person's own scale.
    public readonly Sync<bool> Climb = new();
    public readonly Sync<float> GripRadius = new();
    public readonly Sync<float> FlingScale = new();

    private readonly HandHold _hold = new();
    private readonly List<Slot> _touching = new();
    private bool _leftWasHeld;
    private bool _rightWasHeld;

    public override LocomotionKind Kind => Fly.Value ? LocomotionKind.Other : LocomotionKind.Walk;

    public override void OnInit()
    {
        base.OnInit();
        GripRadius.Value = 0.1f;
        FlingScale.Value = 1f;
    }
    public override bool CanActivate() => true;

    private CharacterController _characterController = null!;
    private bool _wasJumpPressed;
    private bool _isCrouching;
    private bool _isSprinting;

    protected override void OnActivated()
    {
        base.OnActivated();
        _characterController = Owner?.CharacterController!;
        if (_characterController != null && (Speed.Value > 0f || AirSpeed.Value > 0f || JumpSpeed.Value > 0f))
        {
            _ownPace = (_characterController.Speed, _characterController.AirSpeed, _characterController.JumpSpeed);
            if (Speed.Value > 0f)
                _characterController.Speed = Speed.Value;
            if (AirSpeed.Value > 0f)
                _characterController.AirSpeed = AirSpeed.Value;
            if (JumpSpeed.Value > 0f)
                _characterController.JumpSpeed = JumpSpeed.Value;
        }
        _wasJumpPressed = false;
        _isCrouching = false;
        _isSprinting = false;
        _leftWasHeld = true;
        _rightWasHeld = true;
        if (_characterController != null)
            _characterController.Flying = Fly.Value;

        // Resume gravity/collision in case a non-physical module (noclip) left
        // it suspended. Not while a seat has the user: the seat parked it.
        Owner?.ResumeCharacter();

        // Desktop look needs mouse capture; in VR the headset drives view.
        if (!IsVRActive())
            Owner?.InputState?.SetMouseCaptureRequested(true);
    }

    protected override void OnDeactivated()
    {
        LetGo(fling: false);
        if (_characterController != null)
            _characterController.Flying = false;
        if (_ownPace is { } own && _characterController != null)
            (_characterController.Speed, _characterController.AirSpeed, _characterController.JumpSpeed) = own;
        _ownPace = null;
        _characterController = null!;
        base.OnDeactivated();
    }

    public override void OnModuleUpdate(float delta)
    {
        if (Owner == null || _characterController == null || !_characterController.IsReady)
            return;

        if (InputBlocked)
        {
            LetGo(fling: false);
            _characterController.SetMovementDirection(float3.Zero);
            return;
        }

        // Seated. The legs are parked but the user can still turn where they sit, so the turn axis keeps
        // being serviced and jump is left alone - jump is how the seat lets go.
        if (MovementBlocked)
        {
            LetGo(fling: false);
            _characterController.SetMovementDirection(float3.Zero);
            ApplyTurn(delta);
            return;
        }

        if (InputInterface == null && Engine.Current?.InputInterface != null)
        {
            // The base class does the late bind; this is only a defensive null check.
            return;
        }

        if (Climb.Value && UpdateClimb(delta))
        {
            ApplyTurn(delta);
            return;
        }

        ApplyMovement();
        ApplyTurn(delta);
        ApplyActions();
    }

    // True while a hand holds on. A grip that closes with something solid within reach takes hold; the hand
    // letting go, or the other hand taking hold instead, ends it.
    private bool UpdateClimb(float delta)
    {
        var root = Owner.UserRoot;
        if (root == null || root.IsDestroyed)
            return false;
        var input = InputInterface;
        bool left = HandHold.GripHeld(input, Chirality.Left);
        bool right = HandHold.GripHeld(input, Chirality.Right);
        bool leftClosed = left && !_leftWasHeld;
        bool rightClosed = right && !_rightWasHeld;
        _leftWasHeld = left;
        _rightWasHeld = right;

        if (_hold.Holding && !(_hold.Side == Chirality.Left ? left : right))
            LetGo(fling: true);
        if (rightClosed)
            TryTakeHold(root, Chirality.Right);
        if (leftClosed)
            TryTakeHold(root, Chirality.Left);
        if (!_hold.Holding)
            return false;

        _characterController.SetMovementDirection(float3.Zero);
        _hold.Pull(root, delta);
        return true;
    }

    private void TryTakeHold(UserRoot root, Chirality side)
    {
        if (HandHold.HandSlot(root, side) is not { } hand || HandHold.HandBusy(root, side))
            return;
        float scale = root.GlobalScale > 0f ? root.GlobalScale : 1f;
        float reach = (GripRadius.Value > 0f ? GripRadius.Value : 0.1f) * scale;
        var point = hand.GlobalPosition;
        if (!HandHold.SolidWithin(root, point, reach, _touching))
            return;
        _hold.Begin(side, point);
        _characterController.SetSimulationEnabled(false);
    }

    private void LetGo(bool fling)
    {
        if (!_hold.Holding)
            return;
        var velocity = _hold.Velocity * (FlingScale.Value > 0f ? FlingScale.Value : 1f);
        _hold.End();
        if (_characterController == null)
            return;
        Owner?.ResumeCharacter();
        if (fling)
            _characterController.SetBodyVelocity(velocity);
    }

    private void ApplyMovement()
    {
        var rawAxis = LocomotionInputHelper.ReadMovementAxis(InputInterface);
        if (ExclusiveAxisMode)
            rawAxis = LocomotionInputHelper.SnapToDominantAxis(rawAxis);
        var axis = LocomotionInputHelper.ShapeMovement(rawAxis, MovementDeadzone, MovementExponent);

        if (Fly.Value)
        {
            _characterController.SetMovementDirection(FlyDirection(axis));
            return;
        }

        float3 moveDir = float3.Zero;
        if (axis.LengthSquared > 1e-6f)
        {
            // Left at its length: the controller walks that fraction of its pace, so the stick sets the speed.
            Owner.GetMovementBasis(out var forward, out var right);
            moveDir = forward * axis.y + right * axis.x;
            if (moveDir.LengthSquared > 1f)
                moveDir = moveDir.Normalized;
            if (Owner.SlowWalk)
                moveDir *= LocomotionController.SlowWalkPace;
        }
        _characterController.SetMovementDirection(moveDir);
    }

    // Where the head looks, pitch and all, or the hand when movement follows the hand; the fly axis adds straight
    // up or down.
    private float3 FlyDirection(float2 axis)
    {
        float vertical = LocomotionInputHelper.ReadVerticalAxis(InputInterface);
        var root = Owner.UserRoot;
        float3 look = root != null ? root.HeadFacingRotation * float3.Backward : float3.Backward;
        if (look.LengthSquared < 1e-6f)
            look = float3.Backward;
        look = look.Normalized;
        float3 forward = Owner.TryGetHandMoveDirection(out var hand) ? hand : look;
        float3 right = float3.Cross(look, float3.Up);
        right = right.LengthSquared < 1e-4f ? float3.Right : right.Normalized;
        float3 move = forward * axis.y + right * axis.x + float3.Up * vertical;
        if (move.LengthSquared > 1f)
            move = move.Normalized;
        if (Owner.SlowWalk)
            move *= LocomotionController.SlowWalkPace;
        return move;
    }

    private void ApplyTurn(float delta)
    {
        // Desktop has no stick; ReadTurnAxis returns 0 and TurnSubmodule no-ops.
        // VR feeds right-stick X here.
        float axis = LocomotionInputHelper.ReadTurnAxis(InputInterface);
        Turn.Update(axis, delta);
    }

    private void ApplyActions()
    {
        if (Fly.Value)
        {
            bool sprint = LocomotionInputHelper.ReadSprint(InputInterface);
            if (sprint != _isSprinting)
            {
                _isSprinting = sprint;
                _characterController.SetSprinting(sprint);
            }
            return;
        }

        bool jumpPressed = LocomotionInputHelper.ReadJump(InputInterface);
        if (jumpPressed && !_wasJumpPressed)
            _characterController.RequestJump();
        _wasJumpPressed = jumpPressed;

        bool crouchPressed = LocomotionInputHelper.ReadCrouch(InputInterface);
        if (crouchPressed != _isCrouching)
        {
            _isCrouching = crouchPressed;
            _characterController.SetCrouching(_isCrouching);
        }

        bool sprintPressed = LocomotionInputHelper.ReadSprint(InputInterface);
        bool wantsSprint = sprintPressed && !_isCrouching;
        if (wantsSprint != _isSprinting)
        {
            _isSprinting = wantsSprint;
            _characterController.SetSprinting(_isSprinting);
        }
    }

    private bool IsVRActive()
    {
        var input = InputInterface ?? Engine.Current?.InputInterface;
        if (input == null) return false;
        bool controllersTracked = input.LeftController?.IsTracked == true || input.RightController?.IsTracked == true;
        bool headTracked = input.HeadDevice?.IsTracked == true;
        return input.VR_Active && (controllersTracked || headTracked);
    }
}
