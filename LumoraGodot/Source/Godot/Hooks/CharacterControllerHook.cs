using Godot;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for CharacterController component → Godot CharacterBody3D.
/// Platform physics hook for Godot.
/// Also syncs XROrigin3D position with UserRoot for proper VR tracking.
/// </summary>
public class CharacterControllerHook : ComponentHook<CharacterController>
{
    private CharacterBody3D _characterBody;
    private Dictionary<object, CollisionShape3D> _collisionShapes = new Dictionary<object, CollisionShape3D>();
    private Vector3 _velocity;
    private Vector3 _moveDirection;
    private bool _jumpRequested;
    private XROrigin3D _xrOrigin;
    private bool _isLocalUser;

    public CharacterBody3D GodotCharacterBody => _characterBody;

    public override void Initialize()
    {
        base.Initialize();

        _characterBody = new CharacterBody3D();
        _characterBody.Name = "CharacterController";

        // Parent the physics body under the world root (not the slot) to avoid double transforms
        Node3D worldRoot = Owner?.World?.GodotSceneRoot as Node3D;
        Node parentNode = (Node)worldRoot ?? attachedNode;
        parentNode.AddChild(_characterBody);

        // Spawn the body at the slot's current transform so we start where the slot is placed
        _characterBody.GlobalTransform = attachedNode.GlobalTransform;

        _characterBody.FloorStopOnSlope = true;
        _characterBody.FloorMaxAngle = Mathf.DegToRad(45f);
        _characterBody.WallMinSlideAngle = Mathf.DegToRad(15f);

        // Check if this is the local user
        var userRoot = Owner.Slot.GetComponent<UserRoot>();
        _isLocalUser = userRoot?.ActiveUser == Owner.World?.LocalUser;

        // Find XROrigin3D in scene tree for VR tracking sync
        if (_isLocalUser)
        {
            _xrOrigin = FindXROrigin();

            // Do initial VR tracking sync at spawn position
            var spawnPos = Owner.Slot.GlobalPosition;
            SyncVRTracking(spawnPos);
        }
    }

    /// <summary>
    /// Find the XROrigin3D in the scene tree.
    /// </summary>
    private XROrigin3D FindXROrigin()
    {
        // Search from scene root
        var root = attachedNode?.GetTree()?.Root;
        if (root == null) return null;
        return FindNodeOfType<XROrigin3D>(root);
    }

    private T FindNodeOfType<T>(Node node) where T : Node
    {
        if (node is T result)
            return result;

        foreach (var child in node.GetChildren())
        {
            var found = FindNodeOfType<T>(child);
            if (found != null)
                return found;
        }
        return null;
    }

    public override void ApplyChanges()
    {
        if (_characterBody == null || !_characterBody.IsInsideTree())
            return;

        // If the owner is not ready, skip processing
        if (Owner == null)
            return;

        // Get delta time from the UpdateManager
        float delta = Owner?.World?.UpdateManager?.DeltaTime ?? (1f / 60f);

        // Apply gravity
        if (_characterBody.IsOnFloor())
        {
            // Clamp downward velocity to prevent bouncing
            if (_velocity.Y < 0)
                _velocity.Y = 0;
        }
        else
        {
            // Apply gravity
            _velocity.Y -= 9.81f * delta * 2.0f; // 2x gravity for snappier feel
        }

        // Apply movement
        if (_moveDirection.LengthSquared() > 0.001f)
        {
            float speed = _characterBody.IsOnFloor() ? Owner.Speed : Owner.AirSpeed;
            _velocity.X = _moveDirection.X * speed;
            _velocity.Z = _moveDirection.Z * speed;
        }
        else
        {
            // Decelerate to stop
            _velocity.X = Mathf.MoveToward(_velocity.X, 0, Owner.Speed * delta * 10);
            _velocity.Z = Mathf.MoveToward(_velocity.Z, 0, Owner.Speed * delta * 10);
        }

        // Apply jump
        if (_jumpRequested && _characterBody.IsOnFloor())
        {
            _velocity.Y = Owner.JumpSpeed;
            _jumpRequested = false;
            GD.Print("CharacterControllerHook: Jump!");
        }

        // Move character
        _characterBody.Velocity = _velocity;
        _characterBody.MoveAndSlide();
        _velocity = _characterBody.Velocity;

        // Sync position back to slot
        var newPos = new float3(
            _characterBody.GlobalPosition.X,
            _characterBody.GlobalPosition.Y,
            _characterBody.GlobalPosition.Z
        );
        Owner.Slot.GlobalPosition = newPos;

        // Sync XROrigin3D and GlobalTrackingSpace for VR (local user only)
        if (_isLocalUser)
        {
            SyncVRTracking(newPos);
        }

        // Immediately propagate the new slot transform so child visuals/cameras follow the body this frame
        slotHook?.ApplyChanges();
    }

    /// <summary>
    /// Sync XROrigin3D position and GlobalTrackingSpace with UserRoot position.
    /// This ensures VR tracking is relative to the user's locomotion position.
    /// </summary>
    private void SyncVRTracking(float3 userPosition)
    {
        // Update XROrigin3D position so VR camera follows user locomotion
        if (_xrOrigin != null && GodotObject.IsInstanceValid(_xrOrigin))
        {
            _xrOrigin.GlobalPosition = new Vector3(userPosition.x, userPosition.y, userPosition.z);
        }

        // Update GlobalTrackingSpace so tracked device positions are transformed correctly
        var inputInterface = Lumora.Core.Engine.Current?.InputInterface;
        if (inputInterface?.GlobalTrackingSpace != null)
        {
            inputInterface.GlobalTrackingSpace.Position = userPosition;
            // Also sync rotation if UserRoot has rotation
            inputInterface.GlobalTrackingSpace.Rotation = Owner.Slot.GlobalRotation;
        }
    }

    public void SetMovementDirection(float3 direction)
    {
        _moveDirection = new Vector3(direction.x, direction.y, direction.z);
    }

    public void RequestJump()
    {
        _jumpRequested = true;
    }

    public void Teleport(float3 position)
    {
        if (_characterBody != null)
        {
            _characterBody.GlobalPosition = new Vector3(position.x, position.y, position.z);
            _velocity = Vector3.Zero;
        }
    }

    public bool IsOnFloor()
    {
        return _characterBody != null && _characterBody.IsOnFloor();
    }

    public void AddColliderShape(object collider)
    {
        if (_collisionShapes.ContainsKey(collider))
        {
            return;
        }

        if (_characterBody == null)
        {
            AquaLogger.Error("CharacterControllerHook: _characterBody is null!");
            return;
        }

        CollisionShape3D collisionShape = new CollisionShape3D();
        collisionShape.Name = $"Shape_{collider.GetType().Name}";
        if (collider is BoxCollider boxCollider)
        {
            BoxShape3D boxShape = new BoxShape3D();
            float3 size = boxCollider.Size.Value;
            boxShape.Size = new Vector3(size.x, size.y, size.z);
            collisionShape.Shape = boxShape;
        }
        else if (collider is CapsuleCollider capsuleCollider)
        {
            CapsuleShape3D capsuleShape = new CapsuleShape3D();
            capsuleShape.Height = capsuleCollider.Height.Value;
            capsuleShape.Radius = capsuleCollider.Radius.Value;
            collisionShape.Shape = capsuleShape;
        }
        else if (collider is SphereCollider sphereCollider)
        {
            SphereShape3D sphereShape = new SphereShape3D();
            sphereShape.Radius = sphereCollider.Radius.Value;
            collisionShape.Shape = sphereShape;
        }
        else
        {
            AquaLogger.Warn($"CharacterControllerHook: Unknown collider type {collider.GetType().Name}");
        }

        // Apply collider offset so shapes line up with the avatar body
        if (collider is Collider baseCollider)
        {
            var offset = baseCollider.Offset.Value;
            collisionShape.Position = new Vector3(offset.x, offset.y, offset.z);
        }

        _characterBody.AddChild(collisionShape);
        _collisionShapes[collider] = collisionShape;
    }

    public void RemoveColliderShape(object collider)
    {
        if (_collisionShapes.TryGetValue(collider, out CollisionShape3D shape))
        {
            shape.QueueFree();
            _collisionShapes.Remove(collider);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _characterBody != null && GodotObject.IsInstanceValid(_characterBody))
        {
            _characterBody.QueueFree();
        }

        _characterBody = null;
        _collisionShapes.Clear();

        base.Destroy(destroyingWorld);
    }
}
