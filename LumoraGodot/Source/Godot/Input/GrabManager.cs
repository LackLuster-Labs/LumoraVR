using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Source.Input;

/// <summary>
/// Simple grab interaction for physics-backed objects marked with Grabbable.
/// Uses a raycast from the right hand (VR) or camera (desktop).
/// </summary>
public partial class GrabManager : Node3D
{
    private const float MaxGrabDistance = 10f;
    private const uint GrabCollisionMask = 1u << 0;

    private RayCast3D _raycast;
    private Camera3D _camera;

    private Slot _grabbedSlot;
    private Lumora.Core.Components.RigidBody _grabbedRigidBody;
    private Vector3 _offsetLocal;
    private Quaternion _rotationOffset = Quaternion.Identity;
    private bool _followRotation;
    private bool _wasGrabPressed;

    public override void _Ready()
    {
        _raycast = new RayCast3D
        {
            Name = "GrabRaycast",
            TargetPosition = new Vector3(0, 0, -MaxGrabDistance),
            CollisionMask = GrabCollisionMask,
            CollideWithAreas = false,
            CollideWithBodies = true,
            Enabled = true
        };
        AddChild(_raycast);
    }

    public override void _Process(double delta)
    {
        var input = IInputProvider.Instance;
        if (input == null)
            return;

        bool grabPressed = input.GetRightGripInput;

        UpdateRay(input);

        if (grabPressed && !_wasGrabPressed)
        {
            TryBeginGrab(input);
        }
        else if (!grabPressed && _wasGrabPressed)
        {
            EndGrab();
        }

        if (_grabbedSlot != null)
        {
            UpdateGrabbedTransform(input);
        }

        _wasGrabPressed = grabPressed;
    }

    private void UpdateRay(IInputProvider input)
    {
        if (input.IsVR)
        {
            var pos = input.GetLimbPosition(IInputProvider.InputLimb.RightHand);
            var rot = input.GetLimbRotation(IInputProvider.InputLimb.RightHand);
            _raycast.GlobalPosition = pos;
            _raycast.GlobalRotation = rot.GetEuler();
        }
        else
        {
            // Check if cached camera is still valid
            if (_camera == null || !GodotObject.IsInstanceValid(_camera))
                _camera = GetViewport()?.GetCamera3D();
            if (_camera == null || !GodotObject.IsInstanceValid(_camera))
                return;

            _raycast.GlobalPosition = _camera.GlobalPosition;
            _raycast.GlobalRotation = _camera.GlobalRotation;
        }

        _raycast.ForceRaycastUpdate();
    }

    private void TryBeginGrab(IInputProvider input)
    {
        if (!_raycast.IsColliding())
            return;

        var collider = _raycast.GetCollider() as Node;
        if (collider == null || !collider.HasMeta("LumoraSlotRef"))
            return;

        var refString = collider.GetMeta("LumoraSlotRef").ToString();
        if (!ulong.TryParse(refString, out ulong rawRef))
            return;

        var world = Lumora.Core.Engine.Current?.WorldManager?.FocusedWorld;
        var slot = world?.ReferenceController?.GetObjectOrNull(new RefID(rawRef)) as Slot;
        if (slot == null)
            return;

        var grabbable = slot.GetComponent<Grabbable>();
        if (grabbable == null || !grabbable.Enabled.Value || !grabbable.AllowGrab.Value)
            return;

        if (!TryGetGrabTransform(input, out var grabPos, out var grabRot))
            return;

        var slotPos = slot.GlobalPosition;
        var slotRot = slot.GlobalRotation;

        _grabbedSlot = slot;
        _followRotation = grabbable.FollowRotation.Value;

        _offsetLocal = grabRot.Inverse() * (new Vector3(slotPos.x, slotPos.y, slotPos.z) - grabPos);
        var slotQuat = new Quaternion(slotRot.x, slotRot.y, slotRot.z, slotRot.w);
        _rotationOffset = grabRot.Inverse() * slotQuat;

        // Disable physics while grabbed
        _grabbedRigidBody = slot.GetComponent<Lumora.Core.Components.RigidBody>();
        if (_grabbedRigidBody != null)
        {
            _grabbedRigidBody.IsKinematic.Value = true;
        }

        AquaLogger.Log($"GrabManager: Grabbed '{slot.SlotName.Value}'");
    }

    private void UpdateGrabbedTransform(IInputProvider input)
    {
        if (_grabbedSlot == null)
            return;

        if (!TryGetGrabTransform(input, out var grabPos, out var grabRot))
            return;

        var worldPos = grabPos + (grabRot * _offsetLocal);
        var newPos = new float3(worldPos.X, worldPos.Y, worldPos.Z);
        var newRot = _followRotation ? new floatQ((grabRot * _rotationOffset).X, (grabRot * _rotationOffset).Y, (grabRot * _rotationOffset).Z, (grabRot * _rotationOffset).W) : (floatQ?)null;
        var slot = _grabbedSlot;
        var followRot = _followRotation;

        // Queue modifications to run during world update with proper locking
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

    private bool TryGetGrabTransform(IInputProvider input, out Vector3 pos, out Quaternion rot)
    {
        if (input.IsVR)
        {
            pos = input.GetLimbPosition(IInputProvider.InputLimb.RightHand);
            rot = input.GetLimbRotation(IInputProvider.InputLimb.RightHand);
            return true;
        }

        _camera ??= GetViewport()?.GetCamera3D();
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
            AquaLogger.Log($"GrabManager: Released '{_grabbedSlot.SlotName.Value}'");
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
    }
}
