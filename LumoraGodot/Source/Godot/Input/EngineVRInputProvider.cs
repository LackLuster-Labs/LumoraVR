// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

﻿using Godot;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Source.Input;

/// <summary>
/// Input provider bridge that exposes Lumora InputInterface tracking/controller data
/// to Godot-side systems (laser UI, grabbing, dashboard placement) in VR mode.
/// </summary>
public partial class EngineVRInputProvider : Node3D, IInputProvider
{
    private InputInterface _input;
    private Node3D _movementRoot;
    private Vector3 _lastHeadPosition = Vector3.Zero;
    private Vector3 _playspaceDelta = Vector3.Zero;
    private bool _hasHeadSample;

    public void Initialize(InputInterface input, Node3D movementRoot = null)
    {
        _input = input;
        _movementRoot = movementRoot;
        IInputProvider.Instance = this;
    }

    public override void _ExitTree()
    {
        if (object.ReferenceEquals(IInputProvider.Instance, this))
            IInputProvider.Instance = null;

        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);

        var head = _input?.GetBodyNode(BodyNode.Head);
        if (head == null || !head.IsTracking)
        {
            _playspaceDelta = Vector3.Zero;
            _hasHeadSample = false;
            return;
        }

        var headPos = ToVector3(head.Position);
        if (_hasHeadSample)
        {
            var deltaFlat = headPos - _lastHeadPosition;
            deltaFlat.Y = 0f;
            _playspaceDelta = deltaFlat;
        }
        else
        {
            _playspaceDelta = Vector3.Zero;
            _hasHeadSample = true;
        }

        _lastHeadPosition = headPos;
    }

    public bool IsVR => _input?.IsVRActive ?? false;
    public Vector3 GetPlayspaceMovementDelta => _playspaceDelta;

    public Vector2 GetMovementInputAxis
    {
        get
        {
            var left = _input?.LeftController;
            if (left == null) return Vector2.Zero;
            return new Vector2(left.ThumbstickPosition.X, -left.ThumbstickPosition.Y);
        }
    }

    public bool GetJumpInput => _input?.LeftController?.PrimaryButtonPressed ?? false;

    public bool GetSprintInput
    {
        get
        {
            var left = _input?.LeftController;
            var right = _input?.RightController;
            if (left == null || right == null) return false;

            float leftMagSq = (left.ThumbstickPosition.X * left.ThumbstickPosition.X) + (left.ThumbstickPosition.Y * left.ThumbstickPosition.Y);
            float rightMagSq = (right.ThumbstickPosition.X * right.ThumbstickPosition.X) + (right.ThumbstickPosition.Y * right.ThumbstickPosition.Y);
            return leftMagSq >= 0.5f && rightMagSq >= 0.5f;
        }
    }

    public float GetHeight
    {
        get
        {
            var head = _input?.GetBodyNode(BodyNode.Head);
            return (head != null && head.IsTracking) ? head.Position.y : 1.8f;
        }
    }

    public Vector3 GetLimbPosition(IInputProvider.InputLimb limb)
    {
        BodyNode node = limb switch
        {
            IInputProvider.InputLimb.Head => BodyNode.Head,
            IInputProvider.InputLimb.LeftHand => BodyNode.LeftHand,
            IInputProvider.InputLimb.RightHand => BodyNode.RightHand,
            IInputProvider.InputLimb.Hip => BodyNode.Hips,
            IInputProvider.InputLimb.LeftFoot => BodyNode.LeftFoot,
            IInputProvider.InputLimb.RightFoot => BodyNode.RightFoot,
            _ => BodyNode.Head
        };

        var tracked = _input?.GetBodyNode(node);
        if (tracked != null && tracked.IsTracking)
            return ToVector3(tracked.Position);

        if (limb == IInputProvider.InputLimb.Hip)
        {
            var head = _input?.GetBodyNode(BodyNode.Head);
            if (head != null && head.IsTracking)
            {
                var headPos = ToVector3(head.Position);
                return headPos + Vector3.Down * 0.9f;
            }
        }

        return Vector3.Zero;
    }

    public Quaternion GetLimbRotation(IInputProvider.InputLimb limb)
    {
        BodyNode node = limb switch
        {
            IInputProvider.InputLimb.Head => BodyNode.Head,
            IInputProvider.InputLimb.LeftHand => BodyNode.LeftHand,
            IInputProvider.InputLimb.RightHand => BodyNode.RightHand,
            IInputProvider.InputLimb.Hip => BodyNode.Hips,
            IInputProvider.InputLimb.LeftFoot => BodyNode.LeftFoot,
            IInputProvider.InputLimb.RightFoot => BodyNode.RightFoot,
            _ => BodyNode.Head
        };

        var tracked = _input?.GetBodyNode(node);
        if (tracked != null && tracked.IsTracking)
            return ToQuaternion(tracked.Rotation);

        return Quaternion.Identity;
    }

    public void MoveTransform(Transform3D transform)
    {
        if (_movementRoot != null && GodotObject.IsInstanceValid(_movementRoot))
        {
            _movementRoot.GlobalTransform = transform;
        }
        else
        {
            GlobalTransform = transform;
        }
    }

    public bool GetLeftGripInput => ReadGripPressed(_input?.LeftController);
    public bool GetRightGripInput => ReadGripPressed(_input?.RightController);
    public bool GetLeftTriggerInput => ReadTriggerPressed(_input?.LeftController);
    public bool GetRightTriggerInput => ReadTriggerPressed(_input?.RightController);
    public bool GetLeftSecondaryInput => (_input?.LeftController?.PrimaryButtonPressed ?? false) || (_input?.LeftController?.SecondaryButtonPressed ?? false);
    public bool GetRightSecondaryInput => _input?.RightController?.SecondaryButtonPressed ?? false;

    private static bool ReadGripPressed(VRController controller)
    {
        return controller != null && (controller.GripPressed || controller.GripValue > 0.5f);
    }

    private static bool ReadTriggerPressed(VRController controller)
    {
        return controller != null && (controller.TriggerPressed || controller.TriggerValue > 0.5f);
    }

    private static Vector3 ToVector3(float3 value)
    {
        return new Vector3(value.x, value.y, value.z);
    }

    private static Quaternion ToQuaternion(floatQ value)
    {
        return new Quaternion(value.x, value.y, value.z, value.w);
    }
}
