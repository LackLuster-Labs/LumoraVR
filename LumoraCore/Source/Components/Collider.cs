using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

/// <summary>
/// Base class for all physics colliders.
/// Standard collider component pattern.
/// </summary>
[ComponentCategory("Physics/Colliders")]
public abstract class Collider : ImplementableComponent
{
    // ===== SYNC FIELDS =====

    public readonly Sync<float3> Offset;
    public readonly Sync<ColliderType> Type;
    public readonly Sync<float> Mass;
    public readonly Sync<bool> CharacterCollider;
    public readonly Sync<bool> IgnoreRaycasts;

    // ===== STATE =====

    protected IColliderOwner _owner;
    protected bool _shapeChanged = true;
    // TODO: Replace with platform-agnostic physics body types - requires physics driver system
    protected object _staticBody; // StaticBody3D in Godot
    protected object _collisionShape; // CollisionShape3D in Godot
    private bool _ownerSearchComplete = false;
    private int _updatesSinceAwake = 0;

    /// <summary>
    /// The owner component that manages this collider (e.g., CharacterController).
    /// </summary>
    public IColliderOwner ColliderOwner => _owner;

    /// <summary>
    /// Local bounds offset after post-processing by owner.
    /// </summary>
    public float3 LocalBoundsOffset
    {
        get
        {
            float3 offset = Offset.Value;
            _owner?.PostprocessBoundsOffset(ref offset);
            return offset;
        }
    }

    // ===== INITIALIZATION =====

    public Collider()
    {
        Offset = new Sync<float3>(this, float3.Zero);
        Type = new Sync<ColliderType>(this, ColliderType.Static);
        Mass = new Sync<float>(this, 1.0f);
        CharacterCollider = new Sync<bool>(this, false);
        IgnoreRaycasts = new Sync<bool>(this, false);
    }

    public override void OnAwake()
    {
        base.OnAwake();

        // Keep hook in sync when collider properties change
        Offset.OnChanged += _ => RunApplyChanges();
        Type.OnChanged += _ => RunApplyChanges();
        Mass.OnChanged += _ => RunApplyChanges();
        CharacterCollider.OnChanged += _ => RunApplyChanges();
        IgnoreRaycasts.OnChanged += _ => RunApplyChanges();
        Slot.LocalPosition.OnChanged += _ => RunApplyChanges();
        Slot.LocalRotation.OnChanged += _ => RunApplyChanges();

        // Lumora Pattern: If this is a CharacterController collider,
        // notify CharacterController components in the SAME slot (not parent slots)
        if (Type.Value == ColliderType.CharacterController)
        {
            foreach (var component in Slot.Components)
            {
                if (component is IColliderOwner owner)
                {
                    _owner = owner;
                    _owner.OnColliderAdded(this);
                    AquaLogger.Log($"Collider: Notified owner {_owner.GetType().Name} in same slot");
                    break; // Only register with first owner found
                }
            }

            if (_owner == null)
            {
                AquaLogger.Warn($"Collider: No IColliderOwner found in same slot '{Slot.SlotName.Value}' for CharacterController collider");
            }
        }
        // For non-CharacterController colliders, register with physics system directly
        // (handled by physics hook)
        RunApplyChanges();
    }

    // ===== UPDATE =====

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Keep searching for owner for first few updates (components might not be awake yet)
        if (!_ownerSearchComplete)
        {
            _updatesSinceAwake++;

            // Re-search for owner if we didn't find one yet (CharacterController colliders only)
            if (_owner == null && _updatesSinceAwake <= 3 && Type.Value == ColliderType.CharacterController)
            {
                // Search same slot for IColliderOwner
                foreach (var component in Slot.Components)
                {
                    if (component is IColliderOwner owner)
                    {
                        _owner = owner;
                        _owner.OnColliderAdded(this);
                        AquaLogger.Log($"Collider: Found owner {_owner.GetType().Name} after {_updatesSinceAwake} updates");
                        _ownerSearchComplete = true;
                        break;
                    }
                }
            }
            else if (_updatesSinceAwake > 3)
            {
                // After 3 updates, give up searching - this is a standalone collider
                _ownerSearchComplete = true;
                if (_owner == null)
                {
                    AquaLogger.Log($"Collider: No owner found after {_updatesSinceAwake} updates, creating standalone static body");
                }
            }
        }

        // TODO: Physics driver system - Create standalone static body if no owner and scene root is ready
        // if (_owner == null && _staticBody == null && _ownerSearchComplete && World.GodotSceneRoot != null)
        // {
        // 	CreateStandaloneStaticBody();
        // }

        // TODO: Physics driver system - Sync position to slot
        // if (_staticBody != null && _staticBody.IsInsideTree())
        // {
        // 	_staticBody.GlobalPosition = Slot.GlobalPosition;
        // 	_staticBody.GlobalRotation = Slot.GlobalRotation;
        // 	var scale = Slot.LocalScale.Value;
        // 	_staticBody.Scale = new global::Godot.Vector3(scale.X, scale.Y, scale.Z);
        // }
    }

    // TODO: Physics driver system - Move to physics hook/driver
    // private void CreateStandaloneStaticBody()
    // {
    // 	_staticBody = new StaticBody3D { Name = $"StaticBody_{GetType().Name}" };
    //
    // 	_collisionShape = new CollisionShape3D { Name = "Shape" };
    // 	_collisionShape.Shape = CreateGodotShape();
    // 	_collisionShape.Position = Offset.Value;
    //
    // 	_staticBody.AddChild(_collisionShape);
    //
    // 	var sceneRoot = World.GodotSceneRoot as Node3D;
    // 	if (sceneRoot != null)
    // 	{
    // 		sceneRoot.AddChild(_staticBody);
    // 		_staticBody.GlobalPosition = Slot.GlobalPosition;
    // 		_staticBody.GlobalRotation = Slot.GlobalRotation;
    // 		var scale = Slot.LocalScale.Value;
    // 		_staticBody.Scale = new global::Godot.Vector3(scale.X, scale.Y, scale.Z);
    // 		AquaLogger.Log($"Collider: Created standalone StaticBody3D for {GetType().Name}");
    // 	}
    // }

    // ===== ABSTRACT METHODS =====

    /// <summary>
    /// Create the collision shape for this collider.
    /// TODO: Physics driver system - Replace Shape3D with platform-agnostic shape type
    /// Called by collider owner when building physics body.
    /// </summary>
    public abstract object CreateGodotShape(); // Returns Shape3D in Godot

    /// <summary>
    /// Get the local bounding box for this collider.
    /// TODO: Physics driver system - Replace Aabb with platform-agnostic AABB type
    /// </summary>
    public abstract object GetLocalBounds(); // Returns Aabb in Godot

    // ===== CLEANUP =====

    public override void OnDestroy()
    {
        _owner?.OnColliderRemoved(this);
        _owner = null;

        // TODO: Physics driver system - Cleanup standalone static body if we created one
        // if (_staticBody != null)
        // {
        // 	_staticBody.QueueFree();
        // 	_staticBody = null;
        // 	_collisionShape = null;
        // }

        base.OnDestroy();
        AquaLogger.Log($"Collider: Destroyed {GetType().Name}");
    }
}
