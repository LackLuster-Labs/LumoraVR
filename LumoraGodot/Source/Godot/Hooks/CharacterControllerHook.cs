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
    private bool _isCrouching;
    private float _currentHeight;
    private float _targetHeight;
    private bool _debugDumpDone;

    public CharacterBody3D GodotCharacterBody => _characterBody;

    public override void Initialize()
    {
        base.Initialize();

        _characterBody = new CharacterBody3D();
        _characterBody.Name = $"@CharacterBody3D_{Owner?.Slot?.Name?.Value ?? "Unknown"}";

       
        // In Lumora/Godot, we parent CharacterBody3D to WORLD ROOT to avoid double transforms.
        // This is because:
        // 1. Godot's CharacterBody3D.MoveAndSlide() operates in world space
        // 2. If parented under a moving slot, the body would get double transforms
        // 3. We sync position BACK to the slot after physics (see ApplyChanges)
        //
        // For remote users: They don't have a local CharacterBody3D - their slot position
        // is driven by network sync, so this doesn't create hierarchy issues.
        //
        // This is a Godot-specific architectural choice, not a bug.
        Node3D worldRoot = Owner?.World?.GodotSceneRoot as Node3D;
        Node parentNode = (Node)worldRoot ?? attachedNode;
        parentNode.AddChild(_characterBody);

        // Spawn the body at the slot's current transform so we start where the slot is placed
        _characterBody.GlobalTransform = attachedNode.GlobalTransform;

        _characterBody.FloorStopOnSlope = true;
        _characterBody.FloorMaxAngle = Mathf.DegToRad(45f);
        _characterBody.WallMinSlideAngle = Mathf.DegToRad(15f);

        // Set collision layers - must match StaticBody3D layers to collide
        _characterBody.CollisionLayer = 1u;
        _characterBody.CollisionMask = 1u;

        // Initialize crouch state
        _currentHeight = Owner.StandingHeight;
        _targetHeight = Owner.StandingHeight;
        _isCrouching = false;

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

    private int CountNodesOfType<T>(Node node) where T : Node
    {
        int count = node is T ? 1 : 0;
        foreach (var child in node.GetChildren())
        {
            count += CountNodesOfType<T>(child);
        }
        return count;
    }

    private void ListNodesOfType<T>(Node node, string typeName) where T : Node
    {
        if (node is T typed)
        {
            var pos = typed is Node3D n3d ? n3d.GlobalPosition : Vector3.Zero;
            int childCount = typed.GetChildCount();
            GD.Print($"  {typeName}: '{typed.Name}' at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}), children={childCount}");

            // For physics bodies, check collision info
            if (typed is CollisionObject3D col)
            {
                GD.Print($"    Layer={col.CollisionLayer}, Mask={col.CollisionMask}");
            }
        }
        foreach (var child in node.GetChildren())
        {
            ListNodesOfType<T>(child, typeName);
        }
    }

    public override void ApplyChanges()
    {
        if (_characterBody == null || !_characterBody.IsInsideTree())
            return;

        // If the owner is not ready, skip processing
        if (Owner == null)
            return;

        // Skip physics processing if the world is not focused (background worlds have ProcessMode.Disabled)
        // When transitioning from Disabled to Inherit, the physics space may not be ready yet
        if (Owner.World?.Focus == World.WorldFocus.Background)
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

        // Smoothly transition crouch height
        if (Mathf.Abs(_currentHeight - _targetHeight) > 0.01f)
        {
            _currentHeight = Mathf.MoveToward(_currentHeight, _targetHeight, Owner.CrouchTransitionSpeed * delta);
            UpdateColliderHeights();
        }

        // Apply movement (use crouch speed when crouching)
        if (_moveDirection.LengthSquared() > 0.001f)
        {
            float speed;
            if (!_characterBody.IsOnFloor())
                speed = Owner.AirSpeed;
            else if (_isCrouching)
                speed = Owner.CrouchSpeed;
            else
                speed = Owner.Speed;

            if (_characterBody.IsOnFloor() && !_isCrouching && Owner.IsSprinting)
            {
                speed *= Owner.SprintMultiplier;
            }
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

        // Move character - check if physics space is valid first
        // When transitioning from ProcessMode.Disabled to Inherit, the physics space may not be ready
        var bodyRid = _characterBody.GetRid();
        var spaceRid = PhysicsServer3D.BodyGetSpace(bodyRid);
        if (!spaceRid.IsValid)
        {
            // Physics space not ready yet, skip this frame
            return;
        }

        _characterBody.Velocity = _velocity;
        _characterBody.MoveAndSlide();
        _velocity = _characterBody.Velocity;

        // Push any rigid bodies we collided with
        int collisionCount = _characterBody.GetSlideCollisionCount();
        for (int i = 0; i < collisionCount; i++)
        {
            var collision = _characterBody.GetSlideCollision(i);
            var collider = collision.GetCollider();
            if (collider is RigidBody3D rigidBody)
            {
                // Apply impulse based on movement direction and mass
                var pushDir = -collision.GetNormal();
                var pushForce = pushDir * Owner.Speed * 0.5f;
                rigidBody.ApplyCentralImpulse(new Vector3(pushForce.X, 0, pushForce.Z));
            }
        }

        // DEBUG: One-time check for RigidBody3D nodes in scene
        if (!_debugDumpDone && _characterBody.IsInsideTree())
        {
            _debugDumpDone = true;
            var root = _characterBody.GetTree().Root;
            int rigidCount = CountNodesOfType<RigidBody3D>(root);
            int staticCount = CountNodesOfType<StaticBody3D>(root);
            GD.Print($"DEBUG PHYSICS BODIES: RigidBody3D={rigidCount}, StaticBody3D={staticCount}");

            // List all RigidBody3D nodes
            ListNodesOfType<RigidBody3D>(root, "RigidBody3D");
        }

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

    /// <summary>
    /// Set the crouching state. Updates target height for smooth transition.
    /// </summary>
    public void SetCrouching(bool crouching)
    {
        if (_isCrouching == crouching)
            return;

        _isCrouching = crouching;
        _targetHeight = crouching ? Owner.CrouchHeight : Owner.StandingHeight;
        AquaLogger.Log($"CharacterControllerHook: Crouch={crouching}, TargetHeight={_targetHeight}");
    }

    /// <summary>
    /// Update all collider shapes to match current height.
    /// </summary>
    private void UpdateColliderHeights()
    {
        foreach (var kvp in _collisionShapes)
        {
            var collider = kvp.Key;
            var shape = kvp.Value;
            if (shape.Shape is CapsuleShape3D capsule && collider is CapsuleCollider capCollider)
            {
                // Just update the capsule height - physics will keep us grounded
                capsule.Height = _currentHeight;

                // Keep original offset from collider component
                var offset = capCollider.Offset.Value;
                shape.Position = new Vector3(offset.x, offset.y, offset.z);
            }
        }
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
