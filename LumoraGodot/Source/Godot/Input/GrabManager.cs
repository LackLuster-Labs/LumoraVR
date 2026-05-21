// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Godot.Hooks;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Input;

/// <summary>
/// Simple grab interaction for physics-backed objects marked with Grabbable.
/// Uses a raycast from the right hand (VR) or camera (desktop).
/// </summary>
public partial class GrabManager : Node3D
{
    private const float MaxGrabDistance = 10f;
    // Layer 1: physics colliders/rigid bodies, Layer 4: UI panel Area3D colliders (inspectors).
    private const uint GrabCollisionMask = (1u << 0) | (1u << 3);
    private enum GrabHand
    {
        None,
        Left,
        Right
    }

    private RayCast3D _leftRaycast;
    private RayCast3D _rightRaycast;
    private Camera3D _camera;

    // Snap rotation to 15 degrees per step when Shift is held while following rotation.
    private const float SnapRotationStepRadians = 0.2617993878f;

    private Slot _grabbedSlot;
    private Lumora.Core.Components.RigidBody _grabbedRigidBody;
    private Vector3 _offsetLocal;
    private Quaternion _rotationOffset = Quaternion.Identity;
    private bool _followRotation;
    private GrabHand _activeHand = GrabHand.None;
    private bool _wasLeftGrabPressed;
    private bool _wasRightGrabPressed;
    private bool _wasFollowToggleHeld;

    public override void _Ready()
    {
        _leftRaycast = CreateRaycast("GrabRaycastLeft");
        _rightRaycast = CreateRaycast("GrabRaycastRight");
        AddChild(_leftRaycast);
        AddChild(_rightRaycast);
    }

    /// <summary>
    /// True when either grab raycast is currently aimed at a slot with an enabled Grabbable.
    /// Drives cursor color / reticle hover state without needing a second physics query.
    /// </summary>
    public bool IsHoveringGrabbable => HasGrabbableTarget(_rightRaycast) || HasGrabbableTarget(_leftRaycast);

    private static bool HasGrabbableTarget(RayCast3D raycast)
    {
        if (raycast == null || !raycast.IsColliding())
            return false;

        if (raycast.GetCollider() is not Node collider)
            return false;

        var slot = ResolveSlotFromCollider(collider);
        if (slot == null)
            return false;

        var grabbable = slot.GetComponent<Grabbable>();
        return grabbable != null && grabbable.Enabled.Value && grabbable.AllowGrab.Value;
    }

    private static RayCast3D CreateRaycast(string name)
    {
        return new RayCast3D
        {
            Name = name,
            TargetPosition = new Vector3(0, 0, -MaxGrabDistance),
            CollisionMask = GrabCollisionMask,
            CollideWithAreas = true,
            CollideWithBodies = true,
            Enabled = true
        };
    }

    public override void _Process(double delta)
    {
        var input = IInputProvider.Instance;
        if (input == null)
            return;

        bool leftGrabPressed = input.GetLeftGripInput;
        bool rightGrabPressed = input.GetRightGripInput;

        UpdateRay(input, GrabHand.Left);
        UpdateRay(input, GrabHand.Right);

        if (_grabbedSlot == null)
        {
            if (rightGrabPressed && !_wasRightGrabPressed)
            {
                TryBeginGrab(input, GrabHand.Right);
            }
            else if (leftGrabPressed && !_wasLeftGrabPressed)
            {
                TryBeginGrab(input, GrabHand.Left);
            }
        }

        if (_grabbedSlot != null)
        {
            bool stillHolding = _activeHand switch
            {
                GrabHand.Left => leftGrabPressed,
                GrabHand.Right => rightGrabPressed,
                _ => false
            };

            if (!stillHolding)
            {
                EndGrab();
            }
            else
            {
                // R toggles "follow rotation" mid-grab so users can switch behaviour
                // without having to drop and re-grab the object.
                bool followToggleHeld = global::Godot.Input.IsKeyPressed(Key.R);
                if (followToggleHeld && !_wasFollowToggleHeld)
                {
                    _followRotation = !_followRotation;
                    if (_followRotation && TryGetGrabTransform(input, _activeHand, out _, out var grabRot))
                    {
                        var slotRot = _grabbedSlot.GlobalRotation;
                        var slotQuat = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
                        _rotationOffset = grabRot.Inverse() * slotQuat;
                    }
                    LumoraLogger.Log($"GrabManager: FollowRotation toggled -> {_followRotation}");
                }
                _wasFollowToggleHeld = followToggleHeld;

                UpdateGrabbedTransform(input, _activeHand);
            }
        }

        _wasLeftGrabPressed = leftGrabPressed;
        _wasRightGrabPressed = rightGrabPressed;
    }

    private void UpdateRay(IInputProvider input, GrabHand hand)
    {
        var raycast = hand == GrabHand.Left ? _leftRaycast : _rightRaycast;
        var limb = hand == GrabHand.Left ? IInputProvider.InputLimb.LeftHand : IInputProvider.InputLimb.RightHand;

        if (raycast == null)
            return;

        if (input.IsVR)
        {
            var pos = input.GetLimbPosition(limb);
            var rot = input.GetLimbRotation(limb);
            raycast.GlobalPosition = pos;
            raycast.GlobalRotation = rot.GetEuler();
        }
        else
        {
            // Check if cached camera is still valid
            if (_camera == null || !GodotObject.IsInstanceValid(_camera))
                _camera = Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
            if (_camera == null || !GodotObject.IsInstanceValid(_camera))
                return;

            raycast.GlobalPosition = _camera.GlobalPosition;
            raycast.GlobalRotation = _camera.GlobalRotation;
        }

        raycast.ForceRaycastUpdate();
    }

    private void TryBeginGrab(IInputProvider input, GrabHand hand)
    {
        var raycast = hand == GrabHand.Left ? _leftRaycast : _rightRaycast;
        if (raycast == null || !raycast.IsColliding())
            return;

        var collider = raycast.GetCollider() as Node;
        if (collider == null)
            return;

        var slot = ResolveSlotFromCollider(collider);
        if (slot == null)
            return;

        var grabbable = slot.GetComponent<Grabbable>();
        if (grabbable == null || !grabbable.Enabled.Value || !grabbable.AllowGrab.Value)
            return;

        if (!TryGetGrabTransform(input, hand, out var grabPos, out var grabRot))
            return;

        var slotPos = slot.GlobalPosition;
        var slotRot = slot.GlobalRotation;

        _grabbedSlot = slot;
        _followRotation = grabbable.FollowRotation.Value;
        _activeHand = hand;

        _offsetLocal = grabRot.Inverse() * (new Vector3(slotPos.x, slotPos.y, slotPos.z) - grabPos);
        var slotQuat = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
        _rotationOffset = grabRot.Inverse() * slotQuat;

        // Disable physics while grabbed
        _grabbedRigidBody = slot.GetComponent<Lumora.Core.Components.RigidBody>();
        if (_grabbedRigidBody != null)
        {
            _grabbedRigidBody.IsKinematic.Value = true;
        }

        LumoraLogger.Log($"GrabManager: Grabbed '{slot.SlotName.Value}' with {hand} hand");
    }

    private static Slot? ResolveSlotFromCollider(Node collider)
    {
        // First try slot-hook lookup (works for inspector/UI Area3D colliders parented under slot node).
        var slot = SlotHook.GetSlotFromNode(collider);
        if (slot != null)
            return slot;

        // Fallback for physics bodies that store explicit slot ref metadata.
        Node? current = collider;
        while (current != null)
        {
            if (current.HasMeta("LumoraSlotRef"))
            {
                var refString = current.GetMeta("LumoraSlotRef").ToString();
                if (ulong.TryParse(refString, out ulong rawRef))
                {
                    var world = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
                    return world?.ReferenceController?.GetObjectOrNull(new RefID(rawRef)) as Slot;
                }
            }

            current = current.GetParent();
        }

        return null;
    }

    private void UpdateGrabbedTransform(IInputProvider input, GrabHand hand)
    {
        if (_grabbedSlot == null)
            return;

        if (!TryGetGrabTransform(input, hand, out var grabPos, out var grabRot))
            return;

        var worldPos = grabPos + (grabRot * _offsetLocal);
        var newPos = new float3(worldPos.X, worldPos.Y, worldPos.Z);

        floatQ? newRot = null;
        if (_followRotation)
        {
            var combined = grabRot * _rotationOffset;
            // Hold Shift while following to snap rotation to 15-degree increments.
            // Useful for desktop builders who want axis-aligned placement. - xlinka
            if (global::Godot.Input.IsKeyPressed(Key.Shift))
            {
                var euler = combined.GetEuler();
                euler = new Vector3(
                    Mathf.Round(euler.X / SnapRotationStepRadians) * SnapRotationStepRadians,
                    Mathf.Round(euler.Y / SnapRotationStepRadians) * SnapRotationStepRadians,
                    Mathf.Round(euler.Z / SnapRotationStepRadians) * SnapRotationStepRadians);
                combined = Quaternion.FromEuler(euler);
            }
            newRot = new floatQ(combined.X, combined.Y, combined.Z, combined.W);
        }

        var slot = _grabbedSlot;
        var followRot = _followRotation;

        slot.World?.RunSynchronously(() =>
        {
            if (slot.IsDestroyed) return;
            slot.GlobalPosition = newPos;
            if (followRot && newRot.HasValue)
            {
                slot.GlobalRotation = newRot.Value;
            }
        });
    }

    private bool TryGetGrabTransform(IInputProvider input, GrabHand hand, out Vector3 pos, out Quaternion rot)
    {
        if (input.IsVR)
        {
            var limb = hand == GrabHand.Left ? IInputProvider.InputLimb.LeftHand : IInputProvider.InputLimb.RightHand;
            pos = input.GetLimbPosition(limb);
            rot = input.GetLimbRotation(limb);
            return true;
        }

        _camera ??= Lumora.Source.Godot.Bootstrap.XRModeManager.Instance?.CurrentCamera;
        if (_camera == null)
        {
            pos = Vector3.Zero;
            rot = Quaternion.Identity;
            return false;
        }

        pos = _camera.GlobalPosition;
        rot = _camera.GlobalTransform.Basis.GetRotationQuaternion();
        return true;
    }

    private void EndGrab()
    {
        if (_grabbedSlot != null)
        {
            LumoraLogger.Log($"GrabManager: Released '{_grabbedSlot.SlotName.Value}'");
        }

        // Re-enable physics when released
        if (_grabbedRigidBody != null)
        {
            _grabbedRigidBody.IsKinematic.Value = false;
            _grabbedRigidBody = null;
        }

        _grabbedSlot = null;
        _rotationOffset = Quaternion.Identity;
        _offsetLocal = Vector3.Zero;
        _followRotation = false;
        _activeHand = GrabHand.None;
        _wasFollowToggleHeld = false;
    }
}
