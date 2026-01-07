using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Physics-based character controller for user movement.
/// Implements IColliderOwner pattern for collision management.
/// </summary>
[ComponentCategory("Physics")]
public class CharacterController : ImplementableComponent, IColliderOwner
{
    // ===== MOVEMENT PARAMETERS =====

    public float Speed { get; set; } = 5.0f;
    public float AirSpeed { get; set; } = 1.0f;
    public float JumpSpeed { get; set; } = 6.0f;
    public float CrouchSpeed { get; set; } = 2.5f;
    public float SprintMultiplier { get; set; } = 1.7f;
    public float StandingHeight { get; set; } = 1.8f;
    public float CrouchHeight { get; set; } = 1.0f;
    public float CrouchTransitionSpeed { get; set; } = 8.0f;

    // ===== STATE =====

    // TODO: Physics driver system - Replace with platform-agnostic physics body
    private object _characterBody; // CharacterBody3D in Godot
    private List<Collider> _colliders = new List<Collider>();
    private Dictionary<Collider, object> _colliderShapes = new Dictionary<Collider, object>(); // CollisionShape3D in Godot
    private bool _collidersChanged = false;

    private float3 _velocity = float3.Zero;
    private float3 _moveDirection = float3.Zero;
    private bool _jumpRequested = false;
    private bool _isReady = false;
    private bool _creationAttempted = false;
    private bool _isCrouching = false;
    private bool _isSprinting = false;
    private float _currentHeight;

    private UserRoot _userRoot;

    public enum MovementState
    {
        Grounded,
        InAir
    }

    private MovementState _currentState = MovementState.InAir;

    // ===== IColliderOwner IMPLEMENTATION =====

    public bool Kinematic => false; // CharacterController is not kinematic

    public void OnColliderAdded(Collider collider)
    {
        if (!_colliders.Contains(collider))
        {
            _colliders.Add(collider);
            _collidersChanged = true;
            AquaLogger.Log($"CharacterController: Added collider {collider.GetType().Name}");
        }
    }

    public void OnColliderRemoved(Collider collider)
    {
        if (_colliders.Remove(collider))
        {
            // TODO: Physics driver - Remove shape from physics body
            if (_colliderShapes.TryGetValue(collider, out var shape))
            {
                // shape.QueueFree(); // Platform-specific cleanup
                _colliderShapes.Remove(collider);
            }
            _collidersChanged = true;
            AquaLogger.Log($"CharacterController: Removed collider {collider.GetType().Name}");
        }
    }

    public void OnColliderShapeChanged(Collider collider)
    {
        AquaLogger.Log($"CharacterController: Rebuilt shape for {collider.GetType().Name} (TODO: Physics driver)");
    }

    public void PostprocessBoundsOffset(ref float3 offset)
    {
    }

    // ===== INITIALIZATION =====

    public override void OnAwake()
    {
        base.OnAwake();

        _userRoot = Slot.GetComponent<UserRoot>();
        if (_userRoot == null)
        {
            AquaLogger.Warn("CharacterController: No UserRoot found!");
            return;
        }
    }

    public override void OnStart()
    {
        base.OnStart();
        TryInitializeLocalUser();
    }

    /// <summary>
    /// Rescan for colliders in the SAME slot only (Lumora pattern).
    /// Only finds colliders with Type == CharacterController.
    /// </summary>
    private void DiscoverColliders()
    {
        _colliders.Clear();

        // Lumora Pattern: Search ONLY the same slot, NOT children
        // Filter for Type == CharacterController colliders only
        foreach (var component in Slot.Components)
        {
            if (component is Collider collider &&
                collider.Enabled &&
                collider.Type.Value == ColliderType.CharacterController)
            {
                _colliders.Add(collider);
                AquaLogger.Log($"CharacterController: Found {collider.GetType().Name} with Type=CharacterController");
            }
        }

        // Sort by some stable criteria if multiple colliders (standard sort order)
        if (_colliders.Count > 1)
        {
            AquaLogger.Warn($"CharacterController: Found {_colliders.Count} colliders, using first one");
        }

        AquaLogger.Log($"CharacterController: Discovered {_colliders.Count} colliders in same slot");
    }

    /// <summary>
    /// Get all discovered colliders (for hook to create collision shapes).
    /// </summary>
    public IReadOnlyList<Collider> GetColliders() => _colliders;

    // TODO: Physics driver system - Move to physics hook
    private void CreateGodotController()
    {
        // _characterBody = new CharacterBody3D
        // {
        // 	Name = "CharacterBody",
        // 	FloorStopOnSlope = true,
        // 	FloorMaxAngle = MathF.PI / 4f, // 45 degrees
        // 	WallMinSlideAngle = MathF.PI / 12f // 15 degrees
        // };
        //
        // // Create collision shapes from colliders
        // foreach (var collider in _colliders)
        // {
        // 	CreateCollisionShape(collider);
        // }
        //
        // // Get scene root from World (renderer sets this)
        // var sceneRoot = World.PhysicsSceneRoot;
        // if (sceneRoot != null)
        // {
        // 	sceneRoot.AddChild(_characterBody);
        // 	_characterBody.GlobalPosition = Slot.GlobalPosition;
        // 	_isReady = true;
        // }
        // else
        // {
        // 	AquaLogger.Error("CharacterController: Could not find scene root - WorldRenderer not initialized?");
        // }
        throw new System.NotImplementedException("Physics driver system required");
    }

    // TODO: Physics driver system - Move to physics hook
    private void CreateCollisionShape(Collider collider)
    {
        // var shape = new CollisionShape3D { Name = $"Shape_{collider.GetType().Name}" };
        // shape.Shape = collider.CreateGodotShape();
        // shape.Position = collider.LocalBoundsOffset;
        // _characterBody.AddChild(shape);
        // _colliderShapes[collider] = shape;
        throw new System.NotImplementedException("Physics driver system required");
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        if (!_isReady)
        {
            TryInitializeLocalUser();
        }

        // Skip if not local user
        if (!(_userRoot?.IsLocalUserRoot ?? false))
            return;

        // Keep registering for hook updates every frame so physics runs continuously
        if (_isReady && Hook != null)
        {
            RunApplyChanges();
        }
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

        // Discover colliders NOW (after all components are added to slot)
        DiscoverColliders();

        // Tell hook to create collision shapes
        if (Hook != null)
        {
            AquaLogger.Log($"CharacterController: Hook exists (type={Hook.GetType().Name}), calling AddColliderShape for {_colliders.Count} colliders");

            try
            {
                var colliders = GetColliders();
                foreach (var collider in colliders)
                {
                    AquaLogger.Log($"CharacterController: Calling AddColliderShape for {collider.GetType().Name}");

                    // Use reflection to verify the method exists
                    var method = Hook.GetType().GetMethod("AddColliderShape");
                    if (method != null)
                    {
                        AquaLogger.Log($"CharacterController: Method AddColliderShape found on hook");
                        method.Invoke(Hook, new object[] { collider });
                        AquaLogger.Log($"CharacterController: Method invoked successfully");
                    }
                    else
                    {
                        AquaLogger.Error($"CharacterController: Method AddColliderShape NOT found on hook type {Hook.GetType().Name}");
                    }
                }

                // IMPORTANT: Register for hook updates so physics actually runs!
                RunApplyChanges();
                AquaLogger.Log("CharacterController: Registered for hook updates - physics enabled!");
            }
            catch (System.Exception ex)
            {
                AquaLogger.Error($"CharacterController: Failed to add collider shape: {ex.Message}\nStack: {ex.StackTrace}");
            }
        }
        else
        {
            AquaLogger.Warn("CharacterController: Hook is null!");
        }

        _isReady = true;
        AquaLogger.Log($"CharacterController: Initialized for local user '{_userRoot.ActiveUser.UserName.Value}' with {_colliders.Count} colliders");
    }

    // TODO: Physics driver system - Move to physics hook
    private void ApplyGravity(float delta)
    {
        // if (_characterBody.IsOnFloor())
        // {
        // 	_currentState = MovementState.Grounded;
        // 	// Clamp downward velocity to prevent bouncing
        // 	if (_velocity.y < 0)
        // 		_velocity.y = 0;
        // }
        // else
        // {
        // 	_currentState = MovementState.InAir;
        // 	// Apply gravity
        // 	_velocity.y -= 9.81f * delta * 2.0f; // 2x gravity for snappier feel
        // }
    }

    // TODO: Physics driver system - Move to physics hook
    private void ApplyMovement(float delta)
    {
        // if (_moveDirection.LengthSquared() > 0.001f)
        // {
        // 	float speed = _currentState == MovementState.Grounded ? Speed : AirSpeed;
        // 	_velocity.x = _moveDirection.x * speed;
        // 	_velocity.z = _moveDirection.z * speed;
        // }
        // else
        // {
        // 	_velocity.x = MathHelpers.MoveToward(_velocity.x, 0, Speed * delta * 10);
        // 	_velocity.z = MathHelpers.MoveToward(_velocity.z, 0, Speed * delta * 10);
        // }
    }

    // TODO: Physics driver system - Move to physics hook
    private void ApplyJump()
    {
        // if (_jumpRequested && _currentState == MovementState.Grounded)
        // {
        // 	_velocity.y = JumpSpeed;
        // 	_jumpRequested = false;
        // 	AquaLogger.Log("CharacterController: Jump!");
        // }
        // else if (!_jumpRequested)
        // {
        // 	// Reset if not requested this frame
        // }
    }

    // TODO: Physics driver system - Move to physics hook
    private void SyncToSlot()
    {
        // if (Slot == null)
        // 	return;
        //
        // Slot.GlobalPosition = _characterBody.GlobalPosition;
    }

    // ===== PUBLIC API (Called by LocomotionController) =====

    /// <summary>
    /// Check if CharacterController is ready (physics body created and in scene tree).
    /// </summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Set movement direction (normalized vector in world space).
    /// Called by locomotion system each frame.
    /// </summary>
    public void SetMovementDirection(float3 direction)
    {
        _moveDirection = direction;

        // Pass to hook for physics (uses dynamic dispatch to avoid direct reference)
        dynamic hook = Hook;
        try
        {
            hook?.SetMovementDirection(direction);
        }
        catch { }
    }

    /// <summary>
    /// Request jump (will jump on next physics frame if grounded).
    /// Called by locomotion system.
    /// </summary>
    public void RequestJump()
    {
        _jumpRequested = true;

        // Pass to hook for physics (uses dynamic dispatch)
        dynamic hook = Hook;
        try
        {
            hook?.RequestJump();
        }
        catch { }
    }

    /// <summary>
    /// Set crouch state.
    /// Called by locomotion system.
    /// </summary>
    public void SetCrouching(bool crouching)
    {
        _isCrouching = crouching;
        if (crouching)
        {
            _isSprinting = false;
        }

        // Pass to hook for physics (uses dynamic dispatch)
        dynamic hook = Hook;
        try
        {
            hook?.SetCrouching(crouching);
        }
        catch { }
    }

    /// <summary>
    /// Set sprint state.
    /// </summary>
    public void SetSprinting(bool sprinting)
    {
        if (_isCrouching)
        {
            _isSprinting = false;
            return;
        }
        _isSprinting = sprinting;
    }

    /// <summary>
    /// Check if character is crouching.
    /// </summary>
    public bool IsCrouching => _isCrouching;
    public bool IsSprinting => _isSprinting;

    /// <summary>
    /// Get current movement speed based on state.
    /// </summary>
    public float GetCurrentSpeed()
    {
        if (_isCrouching) return CrouchSpeed;
        if (_currentState == MovementState.InAir) return AirSpeed;
        return _isSprinting ? Speed * SprintMultiplier : Speed;
    }

    /// <summary>
    /// Teleport character to position.
    /// </summary>
    public void Teleport(float3 position)
    {
        // Pass to hook for physics (uses dynamic dispatch)
        dynamic hook = Hook;
        try
        {
            hook?.Teleport(position);
        }
        catch
        {
            Slot.GlobalPosition = position;
        }
    }

    /// <summary>
    /// Check if character is grounded.
    /// </summary>
    public bool IsGrounded()
    {
        // Query hook for physics state (uses dynamic dispatch)
        dynamic hook = Hook;
        try
        {
            return hook?.IsOnFloor() ?? (_currentState == MovementState.Grounded);
        }
        catch
        {
            return _currentState == MovementState.Grounded;
        }
    }

    /// <summary>
    /// Get current velocity.
    /// TODO: Physics driver system required
    /// </summary>
    public float3 GetVelocity() => _velocity;

    /// <summary>
    /// Get character body transform (for calculating movement direction).
    /// TODO: Physics driver system required - Replace with platform-agnostic transform type
    /// </summary>
    public object GetTransform() => null; // _characterBody?.GlobalTransform ?? Identity;

    // ===== CLEANUP =====

    public override void OnDestroy()
    {
        // TODO: Physics driver - Cleanup physics body
        // _characterBody?.QueueFree();
        _characterBody = null;
        _colliders.Clear();
        _colliderShapes.Clear();

        base.OnDestroy();
        AquaLogger.Log("CharacterController: Destroyed");
    }
}
