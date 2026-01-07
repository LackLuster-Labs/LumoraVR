using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core;

/// <summary>
/// A Slot is the fundamental container for Components and child Slots.
/// Forms a hierarchical structure for organizing objects in a World.
/// </summary>
public class Slot : ContainerWorker<Component>, IImplementable<IHook<Slot>>, IChangeable, IInitializable
{
    #region Fields

    private readonly List<Slot> _children = new();
    private readonly List<Slot> _localChildren = new();
    private readonly List<Component> _components = new();
    private Slot _parent;
    private bool _isRemoved;

    // Transform caching with dirty flags
    private int _transformDirty = 0;
    private float4x4 _cachedTRS = float4x4.Identity;
    private float4x4 _cachedLocalToWorld = float4x4.Identity;
    private float4x4 _cachedWorldToLocal = float4x4.Identity;
    private float3 _cachedGlobalPosition = float3.Zero;
    private floatQ _cachedGlobalRotation = floatQ.Identity;
    private float3 _cachedGlobalScale = float3.One;

    // Transform cache constants
    private const int DIRTY_TRS = 1;
    private const int DIRTY_LOCAL_TO_WORLD = 2;
    private const int DIRTY_WORLD_TO_LOCAL = 4;
    private const int DIRTY_GLOBAL_POSITION = 8;
    private const int DIRTY_GLOBAL_ROTATION = 16;
    private const int DIRTY_GLOBAL_SCALE = 32;
    private const int DIRTY_ALL_GLOBAL = DIRTY_LOCAL_TO_WORLD | DIRTY_WORLD_TO_LOCAL |
                                          DIRTY_GLOBAL_POSITION | DIRTY_GLOBAL_ROTATION | DIRTY_GLOBAL_SCALE;

    // Scheduled actions
    private readonly Queue<Action> _scheduledActions = new();
    private readonly object _scheduleLock = new();

    #endregion

    #region Events

    /// <summary>
    /// Event fired when this slot changes.
    /// </summary>
    public event Action<IChangeable> Changed;

    /// <summary>
    /// Event fired when a child is added.
    /// </summary>
    public event Action<Slot, Slot> OnChildAdded;

    /// <summary>
    /// Event fired when a child is removed.
    /// </summary>
    public event Action<Slot, Slot> OnChildRemoved;

    /// <summary>
    /// Event fired when a component is added.
    /// </summary>
    public event Action<Slot, Component> OnComponentAdded;

    /// <summary>
    /// Event fired when a component is removed.
    /// </summary>
    public event Action<Slot, Component> OnComponentRemoved;

    /// <summary>
    /// Event fired when parent changes.
    /// </summary>
    public event Action<Slot, Slot, Slot> OnParentChanged;

    /// <summary>
    /// Event fired when active state changes.
    /// </summary>
    public event Action<Slot, bool> OnActiveChanged;

    /// <summary>
    /// Event fired when name changes.
    /// </summary>
    public event Action<Slot, string> OnNameChanged;

    /// <summary>
    /// Event fired when children order is invalidated.
    /// </summary>
    public event Action<Slot> ChildrenOrderInvalidated;

    // Simplified event aliases for UI compatibility
    public event Action<Slot> ActiveChanged;
    public event Action<Slot> ParentChanged;

    #endregion

    #region Sync Fields

    /// <summary>
    /// Name of this Slot (synchronized).
    /// </summary>
    public readonly Sync<string> Name = new();

    /// <summary>
    /// Reference to parent slot (synchronized).
    /// This is how parent-child relationships are synced over the network.
    /// </summary>
    [NameOverride("Parent")]
    public readonly SyncRef<Slot> ParentSlotRef = new();

    /// <summary>
    /// Tag for categorization and searching.
    /// </summary>
    public readonly Sync<string> Tag = new();

    /// <summary>
    /// Whether this Slot is active locally (synchronized).
    /// </summary>
    [NameOverride("Active")]
    public readonly Sync<bool> ActiveSelf = new();

    /// <summary>
    /// Whether this Slot and its contents should persist when saved.
    /// </summary>
    [NameOverride("Persistent")]
    [NonPersistent]
    public readonly Sync<bool> Persistent = new();

    /// <summary>
    /// Position in local space (synchronized).
    /// </summary>
    [NameOverride("Position")]
    public readonly Sync<float3> LocalPosition = new();

    /// <summary>
    /// Rotation in local space (synchronized).
    /// </summary>
    [NameOverride("Rotation")]
    public readonly Sync<floatQ> LocalRotation = new();

    /// <summary>
    /// Scale in local space (synchronized).
    /// </summary>
    [NameOverride("Scale")]
    public readonly Sync<float3> LocalScale = new();

    /// <summary>
    /// Order offset for sorting children.
    /// </summary>
    [NameOverride("OrderOffset")]
    public readonly Sync<long> OrderOffset = new();

    /// <summary>
    /// Alias for Name for backward compatibility.
    /// </summary>
    public Sync<string> SlotName => Name;

    #endregion

    #region Properties

    /// <summary>
    /// Numeric alias for RefID.
    /// </summary>
    public ulong RefIdNumeric => (ulong)ReferenceID;

    /// <summary>
    /// Whether this Slot persists when saved.
    /// </summary>
    public override bool IsPersistent => Persistent.Value;

    /// <summary>
    /// Get all referenced objects from this slot (IWorker implementation).
    /// </summary>
    public override IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true)
    {
        // Return parent if referenced
        if (ParentSlotRef?.Target != null && (!persistentOnly || ParentSlotRef.Target.IsPersistent))
            yield return ParentSlotRef.Target;

        // Return components
        foreach (var component in _components)
        {
            if (!persistentOnly || component.IsPersistent)
                yield return component;
        }

        // Return children
        foreach (var child in _children)
        {
            if (!persistentOnly || child.IsPersistent)
                yield return child;
        }
    }

    /// <summary>
    /// The hook that implements this slot in the engine (e.g., Godot Node3D).
    /// </summary>
    public IHook<Slot> Hook { get; private set; }

    /// <summary>
    /// Explicit interface implementation for non-generic IHook.
    /// </summary>
    IHook IImplementable.Hook => Hook;

    /// <summary>
    /// Slot refers to itself for IImplementable.
    /// </summary>
    Slot IImplementable.Slot => this;

    /// <summary>
    /// Whether this Slot has been removed from the hierarchy but not destroyed.
    /// </summary>
    public bool IsRemoved => _isRemoved;

    /// <summary>
    /// Read-only list of child Slots.
    /// </summary>
    public IReadOnlyList<Slot> Children => _children.AsReadOnly();

    /// <summary>
    /// Read-only list of local-only child Slots.
    /// </summary>
    public IReadOnlyList<Slot> LocalChildren => _localChildren.AsReadOnly();

    /// <summary>
    /// Number of child Slots.
    /// </summary>
    public int ChildCount => _children.Count;

    /// <summary>
    /// Number of local-only child Slots.
    /// </summary>
    public int LocalChildCount => _localChildren.Count;

    /// <summary>
    /// Read-only list of Components attached to this Slot.
    /// </summary>
    public new IReadOnlyList<Component> Components => _components.AsReadOnly();

    /// <summary>
    /// Number of Components attached to this Slot.
    /// </summary>
    public new int ComponentCount => _components.Count;

    /// <summary>
    /// Whether this is the root slot (has no parent).
    /// </summary>
    public bool IsRootSlot => World != null && ReferenceEquals(World.RootSlot, this);

    /// <summary>
    /// Whether this slot has a pending (unresolved) parent reference.
    /// True if ParentSlotRef has a RefID but the Target hasn't resolved yet.
    /// Used to distinguish between true root slots and slots waiting for parent resolution.
    /// </summary>
    public bool HasPendingParent => _parent == null && ParentSlotRef != null && !ParentSlotRef.Value.IsNull;

    /// <summary>
    /// Whether we're still waiting to know if this slot has a parent.
    /// True if ParentSlotRef hasn't been decoded yet (still in init phase).
    /// During network decode, sync members are decoded separately from slots,
    /// so we can't know the parent until ParentSlotRef is decoded.
    /// </summary>
    public bool IsParentUnknown => _parent == null && ParentSlotRef != null && ParentSlotRef.IsInInitPhase;

    /// <summary>
    /// Whether this slot is truly a root slot (no parent and no pending parent).
    /// Unlike IsRootSlot, this returns false for slots waiting for parent resolution
    /// AND for slots where ParentSlotRef hasn't been decoded yet.
    /// </summary>
    public bool IsTrueRootSlot => _parent == null &&
        ParentSlotRef != null &&
        !ParentSlotRef.IsInInitPhase &&
        ParentSlotRef.Value.IsNull;

    /// <summary>
    /// Get the root slot of this hierarchy.
    /// </summary>
    public Slot Root
    {
        get
        {
            var current = this;
            while (current._parent != null)
                current = current._parent;
            return current;
        }
    }

    /// <summary>
    /// Depth of this slot in the hierarchy (root = 0).
    /// </summary>
    public int Depth
    {
        get
        {
            int depth = 0;
            var current = _parent;
            while (current != null)
            {
                depth++;
                current = current._parent;
            }
            return depth;
        }
    }

    /// <summary>
    /// Index of this slot among its siblings.
    /// </summary>
    public int SiblingIndex => _parent?._children.IndexOf(this) ?? 0;

    /// <summary>
    /// Get the object root (first slot with a specific component like UserRoot).
    /// </summary>
    public Slot ObjectRoot
    {
        get
        {
            var current = this;
            while (current != null)
            {
                // Check for common object root markers
                if (current.GetComponent<ObjectRoot>() != null)
                    return current;
                if (current._parent == null)
                    return current;
                current = current._parent;
            }
            return this;
        }
    }

    /// <summary>
    /// Whether this slot is under the local user's hierarchy.
    /// </summary>
    public bool IsUnderLocalUser
    {
        get
        {
            var current = this;
            while (current != null)
            {
                var userRoot = current.GetComponent<UserRoot>();
                if (userRoot != null)
                {
                    return userRoot.ActiveUser == World?.LocalUser;
                }
                current = current._parent;
            }
            return false;
        }
    }

    /// <summary>
    /// Get the user root if this slot is under one.
    /// </summary>
    public UserRoot GetUserRoot()
    {
        var current = this;
        while (current != null)
        {
            var userRoot = current.GetComponent<UserRoot>();
            if (userRoot != null)
                return userRoot;
            current = current._parent;
        }
        return null;
    }

    /// <summary>
    /// Get the active user if under a user root.
    /// </summary>
    public User ActiveUser => GetUserRoot()?.ActiveUser;

    #endregion

    #region Parent Property

    /// <summary>
    /// The parent Slot in the hierarchy (null if root).
    /// </summary>
    public Slot Parent
    {
        get => _parent;
        set => SetParent(value, preserveGlobalTransform: false);
    }

    /// <summary>
    /// Set parent with option to preserve global transform.
    /// Updates ParentSlotRef which triggers network sync and internal state update.
    /// Warns if changing parent during init phase.
    /// </summary>
    public void SetParent(Slot newParent, bool preserveGlobalTransform = false)
    {
        if (IsRemoved)
        {
            return;
        }

        if (_parent == newParent)
        {
            return;
        }

        if (IsInInitPhase && newParent != null && !newParent.IsInInitPhase)
        {
            throw new InvalidOperationException("Cannot change parent while in initialization phase.");
        }

        if (newParent == null && World != null)
        {
            newParent = World.RootSlot;
        }

        if (newParent != null && newParent.IsRemoved)
        {
            Logging.Logger.Warn($"Trying to assign a removed parent for slot '{Name?.Value}', resetting to root.");
            newParent = World?.RootSlot;
        }

        if (newParent != null && newParent.IsDescendantOf(this))
        {
            return;
        }

        float3 globalPos = float3.Zero;
        floatQ globalRot = floatQ.Identity;
        float3 globalScale = float3.One;

        if (preserveGlobalTransform)
        {
            globalPos = GlobalPosition;
            globalRot = GlobalRotation;
            globalScale = GlobalScale;
        }

        // Update ParentSlotRef - this triggers OnParentSlotRefChanged which updates
        // _parent, child collections, and fires events
        ParentSlotRef.Target = newParent;

        InvalidateGlobalTransforms();

        if (preserveGlobalTransform && newParent != null)
        {
            GlobalPosition = globalPos;
            GlobalRotation = globalRot;
            GlobalScale = globalScale;
        }
    }

    #endregion

    #region Active State

    /// <summary>
    /// Whether this Slot is active (considering parent chain).
    /// </summary>
    public bool IsActive
    {
        get
        {
            if (!ActiveSelf.Value) return false;
            return _parent?.IsActive ?? true;
        }
    }

    /// <summary>
    /// Check if this slot is active and not destroyed.
    /// </summary>
    public bool IsActiveAndEnabled => IsActive && !IsDestroyed && !_isRemoved;

    /// <summary>
    /// Set active state, optionally affecting children.
    /// </summary>
    public void SetActive(bool active, bool recursive = false)
    {
        bool wasActive = ActiveSelf.Value;
        ActiveSelf.Value = active;

        if (wasActive != active)
        {
            OnActiveChanged?.Invoke(this, active);
            ActiveChanged?.Invoke(this);
        }

        if (recursive)
        {
            foreach (var child in _children)
                child.SetActive(active, true);
        }
    }

    #endregion

    #region Transform Properties

    /// <summary>
    /// Position in local space (convenience accessor).
    /// </summary>
    public float3 Position
    {
        get => LocalPosition.Value;
        set => LocalPosition.Value = value;
    }

    /// <summary>
    /// Rotation in local space (convenience accessor).
    /// </summary>
    public floatQ Rotation
    {
        get => LocalRotation.Value;
        set => LocalRotation.Value = value;
    }

    /// <summary>
    /// Quaternion alias for Rotation.
    /// </summary>
    public floatQ Quaternion
    {
        get => LocalRotation.Value;
        set => LocalRotation.Value = value;
    }

    /// <summary>
    /// Scale in local space (convenience accessor).
    /// </summary>
    public float3 Scale
    {
        get => LocalScale.Value;
        set => LocalScale.Value = value;
    }

    /// <summary>
    /// Position in global/world space.
    /// </summary>
    public float3 GlobalPosition
    {
        get
        {
            if ((_transformDirty & DIRTY_GLOBAL_POSITION) != 0)
            {
                EnsureValidLocalToWorld();
                _cachedGlobalPosition = new float3(_cachedLocalToWorld.c3.x, _cachedLocalToWorld.c3.y, _cachedLocalToWorld.c3.z);
                _transformDirty &= ~DIRTY_GLOBAL_POSITION;
            }
            return _cachedGlobalPosition;
        }
        set
        {
            if (_parent == null)
            {
                LocalPosition.Value = value;
                _cachedGlobalPosition = value;
                _transformDirty &= ~DIRTY_GLOBAL_POSITION;
                return;
            }

            var parentScale = _parent.GlobalScale;
            var invParentRot = _parent.GlobalRotation.Inverse;
            var delta = value - _parent.GlobalPosition;
            var unrotated = invParentRot * delta;

            float3 safeScale = new float3(
                parentScale.x == 0 ? 1 : parentScale.x,
                parentScale.y == 0 ? 1 : parentScale.y,
                parentScale.z == 0 ? 1 : parentScale.z
            );

            LocalPosition.Value = new float3(
                unrotated.x / safeScale.x,
                unrotated.y / safeScale.y,
                unrotated.z / safeScale.z
            );

            if ((_parent._transformDirty & DIRTY_ALL_GLOBAL) == 0)
            {
                _cachedGlobalPosition = value;
                _transformDirty &= ~DIRTY_GLOBAL_POSITION;
            }
        }
    }

    /// <summary>
    /// Rotation in global/world space.
    /// </summary>
    public floatQ GlobalRotation
    {
        get
        {
            if ((_transformDirty & DIRTY_GLOBAL_ROTATION) != 0)
            {
                _cachedGlobalRotation = _parent == null
                    ? LocalRotation.Value
                    : _parent.GlobalRotation * LocalRotation.Value;
                _transformDirty &= ~DIRTY_GLOBAL_ROTATION;
            }
            return _cachedGlobalRotation;
        }
        set
        {
            if (_parent == null)
            {
                LocalRotation.Value = value;
                _cachedGlobalRotation = value;
                _transformDirty &= ~DIRTY_GLOBAL_ROTATION;
                return;
            }
            LocalRotation.Value = _parent.GlobalRotation.Inverse * value;
            if ((_parent._transformDirty & DIRTY_GLOBAL_ROTATION) == 0)
            {
                _cachedGlobalRotation = value;
                _transformDirty &= ~DIRTY_GLOBAL_ROTATION;
            }
        }
    }

    /// <summary>
    /// Scale in global/world space.
    /// </summary>
    public float3 GlobalScale
    {
        get
        {
            if ((_transformDirty & DIRTY_GLOBAL_SCALE) != 0)
            {
                if (_parent == null)
                {
                    _cachedGlobalScale = LocalScale.Value;
                }
                else
                {
                    var parentScale = _parent.GlobalScale;
                    var local = LocalScale.Value;
                    _cachedGlobalScale = new float3(parentScale.x * local.x, parentScale.y * local.y, parentScale.z * local.z);
                }
                _transformDirty &= ~DIRTY_GLOBAL_SCALE;
            }
            return _cachedGlobalScale;
        }
        set
        {
            if (_parent == null)
            {
                LocalScale.Value = value;
                _cachedGlobalScale = value;
                _transformDirty &= ~DIRTY_GLOBAL_SCALE;
                return;
            }

            var parentScale = _parent.GlobalScale;
            float3 safeParentScale = new float3(
                parentScale.x == 0 ? 1 : parentScale.x,
                parentScale.y == 0 ? 1 : parentScale.y,
                parentScale.z == 0 ? 1 : parentScale.z
            );

            LocalScale.Value = new float3(
                value.x / safeParentScale.x,
                value.y / safeParentScale.y,
                value.z / safeParentScale.z
            );

            if ((_parent._transformDirty & DIRTY_GLOBAL_SCALE) == 0)
            {
                _cachedGlobalScale = value;
                _transformDirty &= ~DIRTY_GLOBAL_SCALE;
            }
        }
    }

    /// <summary>
    /// Full local TRS matrix.
    /// </summary>
    public float4x4 LocalTransform
    {
        get
        {
            EnsureValidTRS();
            return _cachedTRS;
        }
    }

    /// <summary>
    /// Full global TRS matrix.
    /// </summary>
    public float4x4 GlobalTransform
    {
        get
        {
            EnsureValidLocalToWorld();
            return _cachedLocalToWorld;
        }
    }

    /// <summary>
    /// Transformation matrix from local space to world space.
    /// </summary>
    public float4x4 LocalToWorld
    {
        get
        {
            EnsureValidLocalToWorld();
            return _cachedLocalToWorld;
        }
    }

    /// <summary>
    /// Transformation matrix from world space to local space.
    /// </summary>
    public float4x4 WorldToLocal
    {
        get
        {
            EnsureValidWorldToLocal();
            return _cachedWorldToLocal;
        }
    }

    /// <summary>
    /// Forward direction in global space.
    /// </summary>
    public float3 Forward => GlobalRotation * float3.Forward;

    /// <summary>
    /// Right direction in global space.
    /// </summary>
    public float3 Right => GlobalRotation * float3.Right;

    /// <summary>
    /// Up direction in global space.
    /// </summary>
    public float3 Up => GlobalRotation * float3.Up;

    /// <summary>
    /// Backward direction in global space.
    /// </summary>
    public float3 Backward => GlobalRotation * float3.Backward;

    /// <summary>
    /// Left direction in global space.
    /// </summary>
    public float3 Left => GlobalRotation * float3.Left;

    /// <summary>
    /// Down direction in global space.
    /// </summary>
    public float3 Down => GlobalRotation * float3.Down;

    #endregion

    #region Transform Methods

    private void InvalidateTransformCache()
    {
        _transformDirty |= DIRTY_TRS;
        InvalidateGlobalTransforms();
    }

    private void InvalidateGlobalTransforms()
    {
        if ((_transformDirty & DIRTY_ALL_GLOBAL) == DIRTY_ALL_GLOBAL)
            return;

        _transformDirty |= DIRTY_ALL_GLOBAL;

        foreach (var child in _children)
            child.InvalidateGlobalTransforms();
    }

    private void EnsureValidTRS()
    {
        if ((_transformDirty & DIRTY_TRS) != 0)
        {
            _cachedTRS = float4x4.TRS(LocalPosition.Value, LocalRotation.Value, LocalScale.Value);
            _transformDirty &= ~DIRTY_TRS;
        }
    }

    private void EnsureValidLocalToWorld()
    {
        if ((_transformDirty & DIRTY_LOCAL_TO_WORLD) != 0)
        {
            if (_parent == null)
            {
                EnsureValidTRS();
                _cachedLocalToWorld = _cachedTRS;
            }
            else
            {
                _parent.EnsureValidLocalToWorld();
                EnsureValidTRS();
                _cachedLocalToWorld = _parent._cachedLocalToWorld * _cachedTRS;
            }
            _transformDirty &= ~DIRTY_LOCAL_TO_WORLD;
        }
    }

    private void EnsureValidWorldToLocal()
    {
        if ((_transformDirty & DIRTY_WORLD_TO_LOCAL) != 0)
        {
            EnsureValidLocalToWorld();
            _cachedWorldToLocal = _cachedLocalToWorld.Inverse;
            _transformDirty &= ~DIRTY_WORLD_TO_LOCAL;
        }
    }

    /// <summary>
    /// Transform a point from global space to local space.
    /// </summary>
    public float3 GlobalPointToLocal(float3 globalPoint)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyPoint(globalPoint);
    }

    /// <summary>
    /// Transform a point from global space to local space (ref version).
    /// </summary>
    public float3 GlobalPointToLocal(in float3 globalPoint)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyPoint(in globalPoint);
    }

    /// <summary>
    /// Transform a point from local space to global space.
    /// </summary>
    public float3 LocalPointToGlobal(float3 localPoint)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyPoint(localPoint);
    }

    /// <summary>
    /// Transform a point from local space to global space (ref version).
    /// </summary>
    public float3 LocalPointToGlobal(in float3 localPoint)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyPoint(in localPoint);
    }

    /// <summary>
    /// Transform a direction from global space to local space.
    /// </summary>
    public float3 GlobalDirectionToLocal(float3 globalDirection)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyVector(globalDirection);
    }

    /// <summary>
    /// Transform a direction from local space to global space.
    /// </summary>
    public float3 LocalDirectionToGlobal(float3 localDirection)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyVector(localDirection);
    }

    /// <summary>
    /// Transform a rotation from global space to local space.
    /// </summary>
    public floatQ GlobalRotationToLocal(floatQ globalRotation)
    {
        return GlobalRotation.Inverse * globalRotation;
    }

    /// <summary>
    /// Transform a rotation from local space to global space.
    /// </summary>
    public floatQ LocalRotationToGlobal(floatQ localRotation)
    {
        return GlobalRotation * localRotation;
    }

    /// <summary>
    /// Look at a target position.
    /// </summary>
    public void LookAt(float3 target, float3? up = null)
    {
        var upVec = up ?? float3.Up;
        var direction = (target - GlobalPosition).Normalized;
        if (direction.LengthSquared > 0.0001f)
        {
            GlobalRotation = floatQ.LookRotation(direction, upVec);
        }
    }

    /// <summary>
    /// Rotate by euler angles in degrees.
    /// </summary>
    public void Rotate(float3 eulerAngles, bool worldSpace = false)
    {
        const float Deg2Rad = (float)(System.Math.PI / 180.0);
        var rotation = floatQ.FromEuler(eulerAngles * Deg2Rad);
        if (worldSpace)
        {
            GlobalRotation = rotation * GlobalRotation;
        }
        else
        {
            LocalRotation.Value = LocalRotation.Value * rotation;
        }
    }

    /// <summary>
    /// Translate by offset.
    /// </summary>
    public void Translate(float3 translation, bool worldSpace = false)
    {
        if (worldSpace)
        {
            GlobalPosition += translation;
        }
        else
        {
            LocalPosition.Value += translation;
        }
    }

    /// <summary>
    /// Set local transform from TRS components.
    /// </summary>
    public void SetLocalTransform(float3 position, floatQ rotation, float3 scale)
    {
        LocalPosition.Value = position;
        LocalRotation.Value = rotation;
        LocalScale.Value = scale;
    }

    /// <summary>
    /// Set global transform from TRS components.
    /// </summary>
    public void SetGlobalTransform(float3 position, floatQ rotation, float3 scale)
    {
        GlobalPosition = position;
        GlobalRotation = rotation;
        GlobalScale = scale;
    }

    /// <summary>
    /// Copy transform from another slot.
    /// </summary>
    public void CopyTransformFrom(Slot source, bool global = false)
    {
        if (source == null) return;

        if (global)
        {
            GlobalPosition = source.GlobalPosition;
            GlobalRotation = source.GlobalRotation;
            GlobalScale = source.GlobalScale;
        }
        else
        {
            LocalPosition.Value = source.LocalPosition.Value;
            LocalRotation.Value = source.LocalRotation.Value;
            LocalScale.Value = source.LocalScale.Value;
        }
    }

    /// <summary>
    /// Reset transform to identity.
    /// </summary>
    public void ResetTransform()
    {
        LocalPosition.Value = float3.Zero;
        LocalRotation.Value = floatQ.Identity;
        LocalScale.Value = float3.One;
    }

    #endregion

    #region Constructor & Initialization

    public Slot()
    {
        InitializeSyncFields();

        ComponentAdded += HandleComponentAdded;
        ComponentRemoved += HandleComponentRemoved;
    }

    private void InitializeSyncFields()
    {
        Name.Value = "Slot";
        ActiveSelf.Value = true;
        LocalPosition.Value = float3.Zero;
        LocalRotation.Value = floatQ.Identity;
        LocalScale.Value = float3.One;
        Tag.Value = string.Empty;
        Persistent.Value = true;
        OrderOffset.Value = 0;

        ((ISyncMember)Name).Name = "Name";
        ((ISyncMember)ParentSlotRef).Name = "Parent";
        ((ISyncMember)Tag).Name = "Tag";
        ((ISyncMember)ActiveSelf).Name = "Active";
        ((ISyncMember)Persistent).Name = "Persistent";
        ((ISyncMember)LocalPosition).Name = "Position";
        ((ISyncMember)LocalRotation).Name = "Rotation";
        ((ISyncMember)LocalScale).Name = "Scale";
        ((ISyncMember)OrderOffset).Name = "OrderOffset";
        ((ISyncMember)componentBag).Name = "Components";

        Persistent.MarkNonPersistent();

        LocalPosition.OnChanged += _ => InvalidateTransformCache();
        LocalRotation.OnChanged += _ => InvalidateTransformCache();
        LocalScale.OnChanged += _ => InvalidateTransformCache();
        ActiveSelf.OnChanged += _ =>
        {
            OnActiveChanged?.Invoke(this, ActiveSelf.Value);
            ActiveChanged?.Invoke(this);
            OnChanged();
        };
        Name.OnChanged += _ =>
        {
            OnNameChanged?.Invoke(this, Name.Value);
            OnChanged();
        };

        // When ParentSlotRef changes (from network sync), update internal parent-child structure
        ParentSlotRef.OnTargetChange += OnParentSlotRefChanged;

        // When ParentSlotRef.Value changes (RefID decoded), trigger hook update
        // This handles the case when ParentSlotRef is decoded as RefID.Null (true root slot)
        // where OnTargetChange won't fire because there's no target to resolve
        ParentSlotRef.OnChanged += OnParentSlotRefValueChanged;
    }

    private void HandleComponentAdded(Component component)
    {
        if (component == null)
            return;

        if (!_components.Contains(component))
        {
            _components.Add(component);
        }

        World?.RegisterComponent(component);
        OnComponentAdded?.Invoke(this, component);
        OnChanged();
    }

    private void HandleComponentRemoved(Component component)
    {
        if (component == null)
            return;

        _components.Remove(component);
        World?.UnregisterComponent(component);
        OnComponentRemoved?.Invoke(this, component);
        OnChanged();
    }

    /// <summary>
    /// Called when ParentSlotRef.Value changes (RefID decoded, not target resolution).
    /// Used to trigger hook updates when parent info is first decoded from network.
    /// </summary>
    private void OnParentSlotRefValueChanged(RefID newValue)
    {
        // If we have a hook and parent was unknown, trigger update now that we know the parent ref
        if (Hook != null && !ParentSlotRef.IsInInitPhase)
        {
            Hook.ApplyChanges();
        }
    }

    /// <summary>
    /// Called when ParentSlotRef changes (e.g., from network sync).
    /// Updates the internal _parent field and child collections.
    /// Don't apply null-to-RootSlot fallback during batch decode,
    /// as the real parent may not be registered yet.
    /// </summary>
    private void OnParentSlotRefChanged(SyncRef<Slot> syncRef)
    {
        if (IsDestroyed)
        {
            return;
        }

        if (IsRootSlot)
        {
            Logging.Logger.Warn($"Tried to assign root slot parent. NewParent={syncRef.Target}");
            World?.RunSynchronously(() => syncRef.Target = null);
            return;
        }

        var restoreParent = _parent;
        if (restoreParent == null || restoreParent.IsDestroyed)
        {
            restoreParent = World?.RootSlot;
        }

        var target = syncRef.Target;
        if (target == null)
        {
            if (World?.RootSlot != null)
            {
                Logging.Logger.Warn("New parent is null, resetting to the root slot.");
                World.RunSynchronously(() => syncRef.Target = World.RootSlot);
            }
            return;
        }

        if (target == _parent)
        {
            return;
        }

        if (target == this || target.IsDescendantOf(this))
        {
            Logging.Logger.Warn("New parent is a descendant of this slot, reverting to a safe parent.");
            var resetParent = World?.RootSlot;
            if (restoreParent != null && !restoreParent.IsDescendantOf(this))
            {
                resetParent = restoreParent;
            }

            if (resetParent != null)
            {
                World?.RunSynchronously(() => syncRef.Target = resetParent);
            }
            return;
        }

        var oldParent = _parent;
        if (_parent != null)
        {
            _parent.DetachChildInternal(this);
        }

        target.AttachChildInternal(this);
        _parent = target;

        InvalidateTransformCache();
        OnParentChanged?.Invoke(this, oldParent, target);
        ParentChanged?.Invoke(this);
        OnChanged();

        if (oldParent == null && Hook != null)
        {
            Hook.ApplyChanges();
        }
    }

	/// <summary>
	/// Initialize this Slot with a World context.
	/// </summary>
	public void Initialize(World world)
	{
		if (world == null)
			throw new ArgumentNullException(nameof(world));

        IsInInitPhase = true;
		base.Initialize(world, _parent);
        EndInitializationStageForMembers();

        // CRITICAL: Mark sync members dirty AFTER registration
        // Values may have been set before Initialize() was called (e.g., Name.Value = "User X")
        // At that time, InvalidateSyncElement() did nothing because World was null.
        // Now that sync members are registered, we need to mark them dirty for network sync.
        if (World?.State == World.WorldState.Running)
        {
            InvalidateSyncMembersForNewSlot();
        }

        AttachToParentLists();

        if (IsRootSlot)
        {
            _transformDirty = 0;
            _cachedTRS = float4x4.TRS(LocalPosition.Value, LocalRotation.Value, LocalScale.Value);
            _cachedLocalToWorld = _cachedTRS;
            _cachedWorldToLocal = _cachedTRS.Inverse;
            _cachedGlobalPosition = LocalPosition.Value;
            _cachedGlobalRotation = LocalRotation.Value;
            _cachedGlobalScale = LocalScale.Value;
        }

        RegisterExistingComponents();
        InitializeHook();
        EndInitPhase();
	}

    /// <summary>
    /// Initialize this Slot from network replication with a pre-assigned RefID.
    /// Used by SlotBag when creating slots from network data.
    /// NOTE: This runs on the sync thread - no Godot operations allowed here!
    /// Hook initialization is deferred to the main thread.
    /// </summary>
	internal void InitializeFromReplicator(World world, RefID assignedId)
	{
		if (world == null)
			throw new ArgumentNullException(nameof(world));

        var refController = world.ReferenceController;
        if (refController == null)
        {
            throw new InvalidOperationException("ReferenceController required for replicated slot initialization");
        }

        IsInInitPhase = true;
        refController.AllocationBlockBegin(assignedId);
        try
        {
            base.Initialize(world, _parent);
        }
        finally
        {
            refController.AllocationBlockEnd();
        }

        EndInitializationStageForMembers();

        AttachToParentLists();

        if (IsRootSlot)
        {
            _transformDirty = 0;
            _cachedTRS = float4x4.TRS(LocalPosition.Value, LocalRotation.Value, LocalScale.Value);
            _cachedLocalToWorld = _cachedTRS;
            _cachedWorldToLocal = _cachedTRS.Inverse;
            _cachedGlobalPosition = LocalPosition.Value;
            _cachedGlobalRotation = LocalRotation.Value;
            _cachedGlobalScale = LocalScale.Value;
        }

        // DO NOT call InitializeHook() here! This runs on sync thread.
        // Hook creation must be deferred to the main thread via World.RunSynchronously
        World?.RunSynchronously(() =>
        {
            if (IsDestroyed || World == null)
                return;

            // NOTE: Do NOT call RegisterExistingComponents() here!
            // Network-created slots get their components through WorkerBag network sync,
            // not through pre-attached components. RegisterExistingComponents() is only
            // for locally-created slots that had components attached before Initialize().
            InitializeHook();
        });

        EndInitPhase();
    }

    /// <summary>
    /// Mark all sync members as dirty after slot initialization.
    /// This is needed because sync member values may have been set BEFORE Initialize()
    /// was called (e.g., slot.Name.Value = "User X"), and at that time the sync member
    /// wasn't registered with SyncController yet, so InvalidateSyncElement() did nothing.
    /// </summary>
    private void InvalidateSyncMembersForNewSlot()
    {
        Name?.InvalidateSyncElement();
        ActiveSelf?.InvalidateSyncElement();
        LocalPosition?.InvalidateSyncElement();
        LocalRotation?.InvalidateSyncElement();
        LocalScale?.InvalidateSyncElement();
        Tag?.InvalidateSyncElement();
        Persistent?.InvalidateSyncElement();
        OrderOffset?.InvalidateSyncElement();
        ParentSlotRef?.InvalidateSyncElement();
    }

    private void InitializeHook()
    {
        if (World == null) return;

        Type hookType = World.HookTypes.GetHookType(typeof(Slot));
        if (hookType == null) return;

        Hook = (IHook<Slot>)Activator.CreateInstance(hookType);
        Hook?.AssignOwner(this);
        Hook?.Initialize();
    }

    private void AttachToParentLists()
    {
        if (_parent != null)
        {
            _parent.AttachChildInternal(this);
        }
    }

    private void RegisterExistingComponents()
    {
        if (World == null || _components.Count == 0)
            return;

        var refController = World.ReferenceController;
        if (refController == null)
            return;

        bool startedLocalBlock = false;
        if (IsLocalElement && !refController.IsInLocalAllocation)
        {
            refController.LocalAllocationBlockBegin();
            startedLocalBlock = true;
        }

        var existing = _components.ToArray();
        _components.Clear();

        foreach (var component in existing)
        {
            if (component == null)
                continue;

            var key = refController.PeekID();
            componentBag.Add(key, component, isNewlyCreated: true, skipSync: true);
        }

        if (startedLocalBlock)
        {
            refController.LocalAllocationBlockEnd();
        }
    }

    #endregion

    #region Scheduling

    /// <summary>
    /// Schedule an action to run on the next update.
    /// </summary>
    public void RunInUpdates(int updateCount, Action action)
    {
        if (action == null) return;

        if (updateCount <= 0)
        {
            action();
            return;
        }

        World?.RunInUpdates(updateCount, action);
    }

    /// <summary>
    /// Schedule an action to run synchronously.
    /// </summary>
    public void RunSynchronously(Action action)
    {
        if (action == null) return;
        World?.RunSynchronously(action);
    }

    /// <summary>
    /// Schedule an action for the next frame.
    /// </summary>
    public void RunNextFrame(Action action)
    {
        RunInUpdates(1, action);
    }

    /// <summary>
    /// Process scheduled actions.
    /// </summary>
    internal void ProcessScheduledActions()
    {
        lock (_scheduleLock)
        {
            while (_scheduledActions.Count > 0)
            {
                var action = _scheduledActions.Dequeue();
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"Error in scheduled action: {ex}");
                }
            }
        }
    }

    #endregion

    #region Component Methods

    /// <summary>
    /// Attach a new Component to this Slot.
    /// </summary>
	public T AttachComponent<T>() where T : Component, new()
	{
        if (World == null)
        {
            var component = new T();
            if (!_components.Contains(component))
            {
                _components.Add(component);
            }
            return component;
        }

        return base.AttachComponent<T>();
    }

    /// <summary>
    /// Attach a Component by type.
    /// </summary>
	public Component AttachComponent(Type componentType)
	{
		if (!typeof(Component).IsAssignableFrom(componentType))
			throw new ArgumentException("Type must derive from Component", nameof(componentType));

        if (World == null)
        {
            var component = (Component)Activator.CreateInstance(componentType);
            if (!_components.Contains(component))
            {
                _components.Add(component);
            }
            return component;
        }

        return base.AttachComponent(componentType);
	}

    /// <summary>
    /// Get or attach a component.
    /// </summary>
    public T GetOrAttachComponent<T>() where T : Component, new()
    {
        var existing = GetComponent<T>();
        return existing ?? AttachComponent<T>();
    }

    /// <summary>
    /// Get the first Component of the specified type.
    /// </summary>
    public T GetComponent<T>() where T : Component
    {
        return _components.OfType<T>().FirstOrDefault();
    }

    /// <summary>
    /// Get Component by type.
    /// </summary>
    public Component GetComponent(Type type)
    {
        return _components.FirstOrDefault(c => type.IsInstanceOfType(c));
    }

    /// <summary>
    /// Get all Components of the specified type.
    /// </summary>
    public IEnumerable<T> GetComponents<T>() where T : Component
    {
        return _components.OfType<T>();
    }

    /// <summary>
    /// Get all Components.
    /// </summary>
    public IEnumerable<Component> GetAllComponents()
    {
        return _components;
    }

    /// <summary>
    /// Get all Components implementing the specified interface type T.
    /// Unlike GetComponents&lt;T&gt;, this works with interface types.
    /// </summary>
    public IEnumerable<T> GetComponentsImplementing<T>() where T : class
    {
        return _components.OfType<T>();
    }

    /// <summary>
    /// Try to get a Component.
    /// </summary>
    public bool TryGetComponent<T>(out T component) where T : Component
    {
        component = GetComponent<T>();
        return component != null;
    }

    /// <summary>
    /// Get the first Component of the specified type matching a predicate.
    /// </summary>
    public T GetComponent<T>(Func<T, bool> predicate) where T : Component
    {
        return _components.OfType<T>().FirstOrDefault(predicate);
    }

    /// <summary>
    /// Get Component in parent hierarchy.
    /// </summary>
    public T GetComponentInParent<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            var comp = GetComponent<T>();
            if (comp != null) return comp;
        }

        return _parent?.GetComponentInParent<T>(true);
    }

    /// <summary>
    /// Alias for GetComponentInParent for API compatibility.
    /// </summary>
    public T GetComponentInParents<T>(bool includeSelf = true) where T : Component
        => GetComponentInParent<T>(includeSelf);

    /// <summary>
    /// Get Component in children hierarchy.
    /// </summary>
    public T GetComponentInChildren<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            var comp = GetComponent<T>();
            if (comp != null) return comp;
        }

        foreach (var child in EnumerateAllChildren())
        {
            var comp = child.GetComponentInChildren<T>(true);
            if (comp != null) return comp;
        }

        return null;
    }

    /// <summary>
    /// Get all Components in children hierarchy.
    /// </summary>
    public IEnumerable<T> GetComponentsInChildren<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            foreach (var comp in _components.OfType<T>())
                yield return comp;
        }

        foreach (var child in EnumerateAllChildren())
        {
            foreach (var comp in child.GetComponentsInChildren<T>(true))
                yield return comp;
        }
    }

    /// <summary>
    /// Get all Components in parent hierarchy.
    /// </summary>
    public IEnumerable<T> GetComponentsInParent<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            foreach (var comp in _components.OfType<T>())
                yield return comp;
        }

        if (_parent != null)
        {
            foreach (var comp in _parent.GetComponentsInParent<T>(true))
                yield return comp;
        }
    }

	/// <summary>
	/// Remove a Component from this Slot.
	/// </summary>
	public void RemoveComponent(Component component)
	{
        if (component == null)
            return;

        base.RemoveComponent(component);
	}

    /// <summary>
    /// Remove all Components of a type.
    /// </summary>
    public void RemoveComponents<T>() where T : Component
    {
        var toRemove = _components.OfType<T>().ToArray();
        foreach (var comp in toRemove)
            RemoveComponent(comp);
    }

    /// <summary>
    /// Check if this slot has a component of type.
    /// </summary>
    public bool HasComponent<T>() where T : Component
    {
        return _components.OfType<T>().Any();
    }

    /// <summary>
    /// Iterate all components with an action.
    /// </summary>
    public void ForeachComponent(Action<Component> action)
    {
        foreach (var comp in _components)
            action(comp);
    }

    /// <summary>
    /// Iterate all components in children.
    /// </summary>
    public void ForeachComponentInChildren<T>(Action<T> action, bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            foreach (var comp in _components.OfType<T>())
                action(comp);
        }

        foreach (var child in EnumerateAllChildren())
            child.ForeachComponentInChildren(action, true);
    }

    #endregion

    #region Child Slot Methods

    /// <summary>
    /// Create a new child Slot.
    /// </summary>
    public Slot AddSlot(string name = "Slot")
    {
        if (World == null)
        {
            var uninitializedSlot = new Slot();
            uninitializedSlot.Name.Value = name;
            uninitializedSlot.Parent = this;
            return uninitializedSlot;
        }

        var refController = World.ReferenceController;
        if (refController == null)
            throw new InvalidOperationException("ReferenceController required for slot allocation");

        if (IsLocalElement)
        {
            return AddLocalSlot(name);
        }

        var slot = new Slot();
        slot.Name.Value = name;
        slot.Parent = this;
        slot.Initialize(World);
        World.RegisterSlot(slot);

        return slot;
    }

    /// <summary>
    /// Add a local-only child slot (not synchronized).
    /// </summary>
    public Slot AddLocalSlot(string name = "LocalSlot")
    {
        if (World == null)
            return AddSlot(name);

        var refController = World.ReferenceController;
        refController?.LocalAllocationBlockBegin();

        var slot = new Slot();
        slot.Name.Value = name;
        slot.Parent = this;
        slot.Initialize(World);
        World.RegisterSlot(slot);

        refController?.LocalAllocationBlockEnd();

        return slot;
    }

    private bool ShouldStoreInLocalChildren(Slot child)
    {
        return child != null && child.IsLocalElement && !IsLocalElement;
    }

    private void AttachChildInternal(Slot child)
    {
        if (child == null)
            return;

        var list = ShouldStoreInLocalChildren(child) ? _localChildren : _children;
        if (!list.Contains(child))
        {
            list.Add(child);
            if (IsInInitPhase)
            {
                childInitializables.Add(child);
            }
            OnChildAdded?.Invoke(this, child);
        }
    }

    private void DetachChildInternal(Slot child)
    {
        if (child == null)
            return;

        var list = ShouldStoreInLocalChildren(child) ? _localChildren : _children;
        if (list.Remove(child))
        {
            OnChildRemoved?.Invoke(this, child);
        }
    }

    private IEnumerable<Slot> EnumerateAllChildren()
    {
        foreach (var child in _children)
            yield return child;
        foreach (var child in _localChildren)
            yield return child;
    }

    /// <summary>
    /// Get child by index.
    /// </summary>
    public Slot GetChild(int index)
    {
        if (index < 0 || index >= _children.Count)
            return null;
        return _children[index];
    }

    /// <summary>
    /// Indexer for children.
    /// </summary>
    public Slot this[int index] => GetChild(index);

    /// <summary>
    /// Indexer for children by name.
    /// </summary>
    public Slot this[string name] => GetChild(name);

    /// <summary>
    /// Get child by name (first match).
    /// </summary>
    public Slot GetChild(string name)
    {
        return _children.FirstOrDefault(c => c.Name.Value == name);
    }

    /// <summary>
    /// Find child by name (recursive optional).
    /// </summary>
    public Slot FindChild(string name, bool recursive = false, int maxDepth = -1)
    {
        return FindChild(s => s.Name.Value == name, recursive, maxDepth);
    }

    /// <summary>
    /// Find child by predicate.
    /// </summary>
    public Slot FindChild(Predicate<Slot> predicate, bool recursive = false, int maxDepth = -1)
    {
        foreach (var child in EnumerateAllChildren())
        {
            if (predicate(child))
                return child;

            if (recursive && maxDepth != 0)
            {
                var found = child.FindChild(predicate, true, maxDepth > 0 ? maxDepth - 1 : -1);
                if (found != null)
                    return found;
            }
        }
        return null;
    }

    /// <summary>
    /// Find all children matching predicate.
    /// </summary>
    public IEnumerable<Slot> FindChildren(Predicate<Slot> predicate, bool recursive = false)
    {
        foreach (var child in EnumerateAllChildren())
        {
            if (predicate(child))
                yield return child;

            if (recursive)
            {
                foreach (var found in child.FindChildren(predicate, true))
                    yield return found;
            }
        }
    }

    /// <summary>
    /// Find children by tag.
    /// </summary>
    public IEnumerable<Slot> FindChildrenByTag(string tag, bool recursive = false)
    {
        return FindChildren(s => s.Tag.Value == tag, recursive);
    }

    /// <summary>
    /// Find child or create if not found.
    /// </summary>
    public Slot FindChildOrAdd(string name, bool recursive = false)
    {
        var found = FindChild(name, recursive);
        return found ?? AddSlot(name);
    }

    /// <summary>
    /// Get or create a slot at a relative path.
    /// </summary>
    public Slot GetSlotAtPath(string path, bool createIfMissing = false)
    {
        if (string.IsNullOrEmpty(path))
            return this;

        var parts = path.Split('/');
        var current = this;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part) || part == ".")
                continue;

            if (part == "..")
            {
                current = current._parent ?? current;
                continue;
            }

            var child = current.GetChild(part);
            if (child == null)
            {
                if (!createIfMissing)
                    return null;
                child = current.AddSlot(part);
            }
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Iterate all children.
    /// </summary>
    public void ForeachChild(Action<Slot> action)
    {
        foreach (var child in EnumerateAllChildren())
            action(child);
    }

    /// <summary>
    /// Iterate all descendants.
    /// </summary>
    public void ForeachChildRecursive(Action<Slot> action)
    {
        foreach (var child in EnumerateAllChildren())
        {
            action(child);
            child.ForeachChildRecursive(action);
        }
    }

    /// <summary>
    /// Move a child to a specific index.
    /// </summary>
    public void MoveChildToIndex(Slot child, int index)
    {
        if (!_children.Contains(child))
            return;

        _children.Remove(child);
        index = System.Math.Clamp(index, 0, _children.Count);
        _children.Insert(index, child);
    }

    /// <summary>
    /// Sort children by order offset.
    /// </summary>
    public void SortChildren()
    {
        _children.Sort((a, b) => a.OrderOffset.Value.CompareTo(b.OrderOffset.Value));
    }

    /// <summary>
    /// Sort children by name.
    /// </summary>
    public void SortChildrenByName()
    {
        _children.Sort((a, b) => string.Compare(a.Name.Value, b.Name.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Get all ancestors up to root.
    /// </summary>
    public IEnumerable<Slot> GetAncestors(bool includeSelf = false)
    {
        if (includeSelf)
            yield return this;

        var current = _parent;
        while (current != null)
        {
            yield return current;
            current = current._parent;
        }
    }

    /// <summary>
    /// Get all descendants.
    /// </summary>
    public IEnumerable<Slot> GetDescendants(bool includeSelf = false)
    {
        if (includeSelf)
            yield return this;

        foreach (var child in EnumerateAllChildren())
        {
            yield return child;
            foreach (var desc in child.GetDescendants(false))
                yield return desc;
        }
    }

    /// <summary>
    /// Check if this slot is a descendant of another.
    /// </summary>
    public bool IsDescendantOf(Slot slot)
    {
        if (slot == null) return false;
        var current = _parent;
        while (current != null)
        {
            if (current == slot)
                return true;
            current = current._parent;
        }
        return false;
    }

    /// <summary>
    /// Check if this slot is an ancestor of another.
    /// </summary>
    public bool IsAncestorOf(Slot slot)
    {
        return slot?.IsDescendantOf(this) ?? false;
    }

    /// <summary>
    /// Find common ancestor with another slot.
    /// </summary>
    public Slot FindCommonAncestor(Slot other)
    {
        if (other == null) return null;

        var ancestors = new HashSet<Slot>(GetAncestors(true));
        foreach (var ancestor in other.GetAncestors(true))
        {
            if (ancestors.Contains(ancestor))
                return ancestor;
        }
        return null;
    }

    /// <summary>
    /// Count total descendants.
    /// </summary>
    public int CountDescendants()
    {
        int count = _children.Count + _localChildren.Count;
        foreach (var child in _children)
            count += child.CountDescendants();
        foreach (var child in _localChildren)
            count += child.CountDescendants();
        return count;
    }

    #endregion

    #region Duplication

    /// <summary>
    /// Duplicate this slot and its contents.
    /// </summary>
    public Slot Duplicate(Slot newParent = null, bool preserveGlobalTransform = false)
    {
        newParent ??= _parent;
        if (newParent == null || World == null)
            return null;

        // Create reference mapping for resolving references after duplication
        var refMap = new Dictionary<RefID, RefID>();

        return DuplicateInternal(newParent, preserveGlobalTransform, refMap);
    }

    private Slot DuplicateInternal(Slot newParent, bool preserveGlobalTransform, Dictionary<RefID, RefID> refMap)
    {
        var clone = newParent.AddSlot(Name.Value + " (Copy)");
        refMap[ReferenceID] = clone.ReferenceID;

        // Copy transform
        if (preserveGlobalTransform)
        {
            clone.GlobalPosition = GlobalPosition;
            clone.GlobalRotation = GlobalRotation;
            clone.GlobalScale = GlobalScale;
        }
        else
        {
            clone.LocalPosition.Value = LocalPosition.Value;
            clone.LocalRotation.Value = LocalRotation.Value;
            clone.LocalScale.Value = LocalScale.Value;
        }

        // Copy properties
        clone.ActiveSelf.Value = ActiveSelf.Value;
        clone.Tag.Value = Tag.Value;
        clone.Persistent.Value = Persistent.Value;
        clone.OrderOffset.Value = OrderOffset.Value;

        // Duplicate components with data copying
        foreach (var component in _components)
        {
            var compType = component.GetType();
            var cloneComp = clone.AttachComponent(compType);
            refMap[component.ReferenceID] = cloneComp.ReferenceID;
            CopyComponentData(component, cloneComp, refMap);
        }

        // Duplicate children
        foreach (var child in _children)
        {
            child.DuplicateInternal(clone, false, refMap);
        }

        return clone;
    }

    /// <summary>
    /// Copy all sync field data from source to target component.
    /// </summary>
    private void CopyComponentData(Component source, Component target, Dictionary<RefID, RefID> refMap)
    {
        var type = source.GetType();

        // Get all sync field properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ISyncMember).IsAssignableFrom(p.PropertyType));

        foreach (var prop in properties)
        {
            try
            {
                var sourceField = prop.GetValue(source) as ISyncMember;
                var targetField = prop.GetValue(target) as ISyncMember;

                if (sourceField != null && targetField != null)
                {
                    CopySyncMemberValue(sourceField, targetField, refMap);
                }
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Failed to copy property {prop.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copy value from one sync member to another.
    /// </summary>
    private void CopySyncMemberValue(ISyncMember source, ISyncMember target, Dictionary<RefID, RefID> refMap)
    {
        var sourceType = source.GetType();
        var targetType = target.GetType();

        if (sourceType != targetType) return;

        // Handle SyncRef types - need to remap references
        if (sourceType.IsGenericType && sourceType.GetGenericTypeDefinition() == typeof(SyncRef<>))
        {
            var sourceValue = source.GetValueAsObject();
            if (sourceValue is RefID refId && refMap.TryGetValue(refId, out var newRefId))
            {
                // Set remapped reference
                var valueProperty = targetType.GetProperty("Value");
                valueProperty?.SetValue(target, newRefId);
            }
            return;
        }

        // Handle regular sync fields
        var sourceVal = source.GetValueAsObject();
        if (sourceVal != null)
        {
            var valueProperty = targetType.GetProperty("Value");
            if (valueProperty != null && valueProperty.CanWrite)
            {
                try
                {
                    valueProperty.SetValue(target, sourceVal);
                }
                catch
                {
                    // Value type mismatch, skip
                }
            }
        }
    }

    /// <summary>
    /// Create a deep copy with all references resolved.
    /// </summary>
    public Slot DeepCopy(Slot newParent = null)
    {
        return Duplicate(newParent, false);
    }

    #endregion

    #region Path & Hierarchy

    /// <summary>
    /// Get the hierarchical path of this slot.
    /// </summary>
    public string GetPath()
    {
        if (_parent == null)
            return Name.Value;
        return _parent.GetPath() + "/" + Name.Value;
    }

    /// <summary>
    /// Get path relative to another slot.
    /// </summary>
    public string GetRelativePath(Slot relativeTo)
    {
        if (relativeTo == null || relativeTo == this)
            return ".";

        if (IsDescendantOf(relativeTo))
        {
            var path = new List<string>();
            var current = this;
            while (current != relativeTo)
            {
                path.Insert(0, current.Name.Value);
                current = current._parent;
            }
            return string.Join("/", path);
        }

        // Need to go up to common ancestor
        var ancestor = FindCommonAncestor(relativeTo);
        if (ancestor == null)
            return GetPath(); // No common ancestor, return full path

        var upCount = 0;
        var check = relativeTo;
        while (check != ancestor)
        {
            upCount++;
            check = check._parent;
        }

        var relativePath = GetRelativePath(ancestor);
        var ups = string.Join("/", Enumerable.Repeat("..", upCount));
        return ups + "/" + relativePath;
    }

    /// <summary>
    /// Get a string representation for hierarchy tracing.
    /// </summary>
    public override string ParentHierarchyToString()
    {
        return GetPath();
    }

    /// <summary>
    /// Print hierarchy to string for debugging.
    /// </summary>
    public string PrintHierarchy(int indent = 0)
    {
        var sb = new System.Text.StringBuilder();
        var prefix = new string(' ', indent * 2);
        sb.AppendLine($"{prefix}{Name.Value} [{_components.Count} components]");
        foreach (var child in _children)
        {
            sb.Append(child.PrintHierarchy(indent + 1));
        }
        foreach (var child in _localChildren)
        {
            sb.Append(child.PrintHierarchy(indent + 1));
        }
        return sb.ToString();
    }

    #endregion

    #region Destroy & Cleanup

    /// <summary>
    /// Destroy this Slot and all its children and components.
    /// </summary>
    public void Destroy()
    {
        if (IsDestroyed) return;

        IsDestroyed = true;

        // Destroy all children
        foreach (var child in _children.ToArray())
            child.Destroy();
        foreach (var child in _localChildren.ToArray())
            child.Destroy();

		// Destroy all components
			foreach (var component in _components.ToArray())
			{
				RemoveComponent(component);
			}

		_children.Clear();
		_localChildren.Clear();

        // Remove from parent
        _parent?.DetachChildInternal(this);

        // Unregister from world
        World?.ReferenceController?.UnregisterObject(this);
        World?.UnregisterSlot(this);

        // Dispose hook
        Hook?.Dispose();
    }

    /// <summary>
    /// Remove from hierarchy without destroying.
    /// </summary>
    public void RemoveFromHierarchy()
    {
        _isRemoved = true;
        _parent?.DetachChildInternal(this);
        _parent = null;
    }

    /// <summary>
    /// Destroy all children.
    /// </summary>
    public void DestroyChildren()
    {
        foreach (var child in _children.ToArray())
            child.Destroy();
        foreach (var child in _localChildren.ToArray())
            child.Destroy();
    }

    /// <summary>
    /// Destroy all children matching predicate.
    /// </summary>
    public void DestroyChildren(Predicate<Slot> predicate)
    {
        foreach (var child in _children.ToArray())
        {
            if (predicate(child))
                child.Destroy();
        }
        foreach (var child in _localChildren.ToArray())
        {
            if (predicate(child))
                child.Destroy();
        }
    }

    /// <summary>
    /// Destroy with delay.
    /// </summary>
    public void DestroyDelayed(float seconds)
    {
        int updates = (int)(seconds * 60); // Approximate 60 fps
        RunInUpdates(updates, Destroy);
    }

    #endregion

    #region Update

    /// <summary>
    /// Update all components.
    /// </summary>
    public void UpdateComponents(float delta)
    {
        ProcessScheduledActions();

        foreach (var component in _components)
        {
            if (component.Enabled.Value)
                component.OnUpdate(delta);
        }
    }

    private void OnChanged()
    {
        Changed?.Invoke(this);
    }

    #endregion

    #region Network Serialization

    /// <summary>
    /// Encode slot state for network transmission.
    /// </summary>
    public void Encode(BinaryWriter writer)
    {
        // Write slot header
        writer.Write((ulong)ReferenceID);
        writer.Write((ulong)(_parent?.ReferenceID ?? RefID.Null));

        // Write sync fields
        EncodeSyncField(writer, Name);
        EncodeSyncField(writer, ActiveSelf);
        EncodeSyncField(writer, LocalPosition);
        EncodeSyncField(writer, LocalRotation);
        EncodeSyncField(writer, LocalScale);
        EncodeSyncField(writer, Tag);
        EncodeSyncField(writer, Persistent);
        EncodeSyncField(writer, OrderOffset);

        // Write components
        writer.Write(_components.Count);
        foreach (var comp in _components)
        {
            writer.Write(comp.GetType().AssemblyQualifiedName ?? comp.GetType().FullName ?? "");
            writer.Write((ulong)comp.ReferenceID);
            comp.Encode(writer);
        }

        // Write children count (children encoded separately)
        writer.Write(_children.Count);
        foreach (var child in _children)
        {
            writer.Write((ulong)child.ReferenceID);
        }
    }

    /// <summary>
    /// Decode slot state from network transmission.
    /// </summary>
    public void Decode(BinaryReader reader, Dictionary<RefID, object> refLookup)
    {
        // Read slot header
        var refId = new RefID(reader.ReadUInt64());
        var parentRefId = new RefID(reader.ReadUInt64());

        // Read sync fields
        DecodeSyncField(reader, Name);
        DecodeSyncField(reader, ActiveSelf);
        DecodeSyncField(reader, LocalPosition);
        DecodeSyncField(reader, LocalRotation);
        DecodeSyncField(reader, LocalScale);
        DecodeSyncField(reader, Tag);
        DecodeSyncField(reader, Persistent);
        DecodeSyncField(reader, OrderOffset);

        // Read components
        int compCount = reader.ReadInt32();
        for (int i = 0; i < compCount; i++)
        {
            var typeName = reader.ReadString();
            var compRefId = new RefID(reader.ReadUInt64());

            var type = Type.GetType(typeName);
            if (type != null)
            {
                var comp = AttachComponent(type);
                comp.Decode(reader);
                refLookup[compRefId] = comp;
            }
        }

        // Read children refs (resolved after all slots decoded)
        int childCount = reader.ReadInt32();
        for (int i = 0; i < childCount; i++)
        {
            var childRefId = new RefID(reader.ReadUInt64());
            // Children are resolved in a second pass
        }
    }

    private void EncodeSyncField<T>(BinaryWriter writer, Sync<T> field)
    {
        field?.Encode(writer);
    }

    private void DecodeSyncField<T>(BinaryReader reader, Sync<T> field)
    {
        field?.Decode(reader);
    }

    #endregion

    #region Binary Serialization (File Save/Load)

    /// <summary>
    /// Save this slot to a binary writer.
    /// </summary>
    public void SaveToBinary(BinaryWriter writer)
    {
        // Write header
        writer.Write("SLOT"); // Magic
        writer.Write(1); // Version

        // Write slot data
        writer.Write(Name.Value ?? "");
        writer.Write(ActiveSelf.Value);
        writer.Write(Tag.Value ?? "");
        writer.Write(Persistent.Value);
        writer.Write(OrderOffset.Value);

        // Transform
        WriteFloat3(writer, LocalPosition.Value);
        WriteFloatQ(writer, LocalRotation.Value);
        WriteFloat3(writer, LocalScale.Value);

        // Components
        writer.Write(_components.Count);
        foreach (var comp in _components)
        {
            writer.Write(comp.GetType().AssemblyQualifiedName ?? comp.GetType().FullName ?? "");
            SaveComponentToBinary(writer, comp);
        }

        // Children
        writer.Write(_children.Count);
        foreach (var child in _children)
            child.SaveToBinary(writer);
    }

    /// <summary>
    /// Load this slot from a binary reader.
    /// </summary>
    public void LoadFromBinary(BinaryReader reader)
    {
        // Read header
        var magic = reader.ReadString();
        if (magic != "SLOT")
            throw new InvalidDataException("Invalid slot data format");

        var version = reader.ReadInt32();
        if (version > 1)
            throw new InvalidDataException($"Unsupported slot version: {version}");

        // Read slot data
        Name.Value = reader.ReadString();
        ActiveSelf.Value = reader.ReadBoolean();
        Tag.Value = reader.ReadString();
        Persistent.Value = reader.ReadBoolean();
        OrderOffset.Value = reader.ReadInt64();

        // Transform
        LocalPosition.Value = ReadFloat3(reader);
        LocalRotation.Value = ReadFloatQ(reader);
        LocalScale.Value = ReadFloat3(reader);

        // Components
        int compCount = reader.ReadInt32();
        for (int i = 0; i < compCount; i++)
        {
            var typeName = reader.ReadString();
            var type = Type.GetType(typeName);
            if (type != null)
            {
                var comp = AttachComponent(type);
                LoadComponentFromBinary(reader, comp);
            }
            else
            {
                // Skip unknown component data
                SkipComponentData(reader);
            }
        }

        // Children
        int childCount = reader.ReadInt32();
        for (int i = 0; i < childCount; i++)
        {
            var child = AddSlot();
            child.LoadFromBinary(reader);
        }
    }

    private void SaveComponentToBinary(BinaryWriter writer, Component comp)
    {
        var type = comp.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(ISyncMember).IsAssignableFrom(p.PropertyType))
            .ToArray();

        writer.Write(properties.Length);

        foreach (var prop in properties)
        {
            writer.Write(prop.Name);
            var syncMember = prop.GetValue(comp) as ISyncMember;
            if (syncMember != null)
            {
                var value = syncMember.GetValueAsObject();
                WriteValue(writer, value);
            }
            else
            {
                WriteValue(writer, null);
            }
        }
    }

    private void LoadComponentFromBinary(BinaryReader reader, Component comp)
    {
        var type = comp.GetType();
        int propCount = reader.ReadInt32();

        for (int i = 0; i < propCount; i++)
        {
            var propName = reader.ReadString();
            var value = ReadValue(reader);

            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && typeof(ISyncMember).IsAssignableFrom(prop.PropertyType))
            {
                var syncMember = prop.GetValue(comp) as ISyncMember;
                if (syncMember != null)
                {
                    SetSyncMemberValue(syncMember, value);
                }
            }
        }
    }

    private void SkipComponentData(BinaryReader reader)
    {
        int propCount = reader.ReadInt32();
        for (int i = 0; i < propCount; i++)
        {
            reader.ReadString(); // Property name
            ReadValue(reader); // Value (discarded)
        }
    }

    private void SetSyncMemberValue(ISyncMember member, object value)
    {
        if (value == null) return;

        var type = member.GetType();
        var valueProp = type.GetProperty("Value");
        if (valueProp != null && valueProp.CanWrite)
        {
            try
            {
                var convertedValue = Convert.ChangeType(value, valueProp.PropertyType);
                valueProp.SetValue(member, convertedValue);
            }
            catch
            {
                // Type conversion failed, try direct assignment
                try
                {
                    valueProp.SetValue(member, value);
                }
                catch
                {
                    // Skip incompatible value
                }
            }
        }
    }

    private void WriteValue(BinaryWriter writer, object value)
    {
        if (value == null)
        {
            writer.Write((byte)0); // Null marker
            return;
        }

        var type = value.GetType();

        if (type == typeof(bool))
        {
            writer.Write((byte)1);
            writer.Write((bool)value);
        }
        else if (type == typeof(int))
        {
            writer.Write((byte)2);
            writer.Write((int)value);
        }
        else if (type == typeof(float))
        {
            writer.Write((byte)3);
            writer.Write((float)value);
        }
        else if (type == typeof(double))
        {
            writer.Write((byte)4);
            writer.Write((double)value);
        }
        else if (type == typeof(string))
        {
            writer.Write((byte)5);
            writer.Write((string)value ?? "");
        }
        else if (type == typeof(float3))
        {
            writer.Write((byte)6);
            WriteFloat3(writer, (float3)value);
        }
        else if (type == typeof(floatQ))
        {
            writer.Write((byte)7);
            WriteFloatQ(writer, (floatQ)value);
        }
        else if (type == typeof(float2))
        {
            writer.Write((byte)8);
            var v = (float2)value;
            writer.Write(v.x);
            writer.Write(v.y);
        }
        else if (type == typeof(float4))
        {
            writer.Write((byte)9);
            var v = (float4)value;
            writer.Write(v.x);
            writer.Write(v.y);
            writer.Write(v.z);
            writer.Write(v.w);
        }
        else if (type == typeof(color))
        {
            writer.Write((byte)10);
            var c = (color)value;
            writer.Write(c.r);
            writer.Write(c.g);
            writer.Write(c.b);
            writer.Write(c.a);
        }
        else if (type == typeof(long))
        {
            writer.Write((byte)11);
            writer.Write((long)value);
        }
        else if (type == typeof(RefID))
        {
            writer.Write((byte)12);
            writer.Write((ulong)(RefID)value);
        }
        else if (type.IsEnum)
        {
            writer.Write((byte)13);
            writer.Write(type.AssemblyQualifiedName ?? type.FullName ?? "");
            writer.Write(Convert.ToInt32(value));
        }
        else
        {
            // Unknown type, serialize as string if possible
            writer.Write((byte)255);
            writer.Write(value.ToString() ?? "");
        }
    }

    private object ReadValue(BinaryReader reader)
    {
        var typeCode = reader.ReadByte();

        return typeCode switch
        {
            0 => null,
            1 => reader.ReadBoolean(),
            2 => reader.ReadInt32(),
            3 => reader.ReadSingle(),
            4 => reader.ReadDouble(),
            5 => reader.ReadString(),
            6 => ReadFloat3(reader),
            7 => ReadFloatQ(reader),
            8 => new float2(reader.ReadSingle(), reader.ReadSingle()),
            9 => new float4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            10 => new color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            11 => reader.ReadInt64(),
            12 => new RefID(reader.ReadUInt64()),
            13 => ReadEnumValue(reader),
            255 => reader.ReadString(), // String fallback
            _ => null
        };
    }

    private object ReadEnumValue(BinaryReader reader)
    {
        var typeName = reader.ReadString();
        var intValue = reader.ReadInt32();

        var type = Type.GetType(typeName);
        if (type != null && type.IsEnum)
        {
            return Enum.ToObject(type, intValue);
        }
        return intValue;
    }

    private static void WriteFloat3(BinaryWriter writer, float3 v)
    {
        writer.Write(v.x);
        writer.Write(v.y);
        writer.Write(v.z);
    }

    private static float3 ReadFloat3(BinaryReader reader)
    {
        return new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void WriteFloatQ(BinaryWriter writer, floatQ q)
    {
        writer.Write(q.x);
        writer.Write(q.y);
        writer.Write(q.z);
        writer.Write(q.w);
    }

    private static floatQ ReadFloatQ(BinaryReader reader)
    {
        return new floatQ(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    #endregion

    #region ToString

    public override string ToString()
    {
        return $"Slot({Name.Value}, RefID={ReferenceID}, Children={_children.Count}, Components={_components.Count})";
    }

    #endregion
}
