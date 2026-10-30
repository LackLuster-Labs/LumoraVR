// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;
using Lumora.Core.Physics;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

// Base class for every physics collider. The platform hook builds the real collision shape from the
// typed sync fields; this side owns the data model and the registry entry.
[ComponentCategory("Physics/Colliders")]
public abstract class Collider : ImplementableComponent
{
    // SYNC FIELDS

    public readonly Sync<float3> Offset;
    public readonly Sync<ColliderType> Type;
    public readonly Sync<float> Mass;
    // New colliders opt into blocking a walking body. Saved flags retain their value; world templates
    // opt in explicitly for their floors and props. -xlinka
    public readonly Sync<bool> CharacterCollider;
    public readonly Sync<bool> IgnoreRaycasts;

    // What this surface does to whatever touches it. Friction is its grip: 0 is ice, 1 is the grip every collider
    // had before these existed, more grips harder still. Bounce is how much of a hit's speed comes back: 0 none,
    // 1 all of it. Where two bodies meet, the lower friction of the two holds and the two bounces add up, to 1 at
    // most. The physics engine keeps one of each per body, so a rigid body made of several colliders takes the
    // lowest friction and the highest bounce among them.
    //
    // People walking are neither slowed nor bounced by them: a character moves itself, and what a floor does to
    // walking belongs to its character controller modifiers. On a character's own collider, a trigger or a
    // NoCollision collider they do nothing, as nothing rests on those. -xlinka
    public readonly Sync<float> Friction;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Bounce;

    // The two as the physics engine is given them. It reads a negative friction as "this one wins" and a negative
    // bounce as "soaks up the other's", neither of which the field says, so they stop at zero; bounce stops at 1,
    // past which the engine clamps the pair anyway; and a value that is not a number is the default. -xlinka
    public float SurfaceFriction => float.IsFinite(Friction.Value) ? MathF.Max(0f, Friction.Value) : 1f;
    public float SurfaceBounce => float.IsFinite(Bounce.Value) ? System.Math.Clamp(Bounce.Value, 0f, 1f) : 0f;

    // STATE

    protected IColliderOwner _owner = null!;
    protected bool _shapeChanged = true;
    private int _ownerSearches;

    // The component that manages this collider, e.g. a CharacterController.
    public IColliderOwner ColliderOwner => _owner;

    // The rigid body whose simulated body carries this shape: the nearest one at or above this slot, for a
    // solid collider. Null leaves the collider a body of its own.
    public RigidBody? Body
    {
        get => _body;
        internal set
        {
            _body = value;
            WatchMoves();
        }
    }

    private RigidBody? _body;

    // Something rests on it, pushes against it, or is pushed by it. Triggers and NoCollision only answer
    // queries, and a character collider belongs to its controller.
    public bool IsSolid => Type.Value is ColliderType.Static or ColliderType.Active;

    // Local bounds offset after the owner has post-processed it.
    public float3 LocalBoundsOffset
    {
        get
        {
            float3 offset = Offset.Value;
            _owner?.PostprocessBoundsOffset(ref offset);
            return offset;
        }
    }

    // INITIALIZATION

    public Collider()
    {
        Offset = new Sync<float3>(this, float3.Zero);
        Type = new Sync<ColliderType>(this, ColliderType.Static);
        Mass = new Sync<float>(this, 1.0f);
        CharacterCollider = new Sync<bool>(this, false);
        IgnoreRaycasts = new Sync<bool>(this, false);
        Friction = new Sync<float>(this, 1f);
        Bounce = new Sync<float>(this, 0f);
    }

    public override void OnAwake()
    {
        base.OnAwake();

        // Join this world's collider registry so raycasts don't have to walk the whole tree to find us. -xlinka
        World?.RegisterCollider(this);

        // Keep hook in sync when collider properties change
        Offset.OnChanged += _ => RunApplyChanges();
        Type.OnChanged += _ =>
        {
            // Solid or not decides which body carries it, so the rigid body above looks again.
            RigidBody.Above(Slot)?.MarkCollidersDirty();
            RunApplyChanges();
        };
        Mass.OnChanged += _ => RunApplyChanges();
        CharacterCollider.OnChanged += _ => RunApplyChanges();
        IgnoreRaycasts.OnChanged += _ => RunApplyChanges();
        Friction.OnChanged += _ => RunApplyChanges();
        Bounce.OnChanged += _ => RunApplyChanges();
        // Re-sync whenever this slot's WORLD transform changes - its own local move, an ancestor
        // moving, or a scale change - coalesced to once per frame. (The old per-field local hooks
        // only caught this slot's own position/rotation; ancestor moves and scale were missed.)
        WatchMoves();
        // Its body hangs under the world root, not the slot, so a slot switched off stays solid and still
        // takes a laser until the hook looks again. -xlinka
        Slot.ActiveChanged += OnSlotActiveChanged;

        // A CharacterController collider tells the controller on its own slot (not a parent's) that it is
        // there; every other collider goes straight to the physics hook.
        SearchForOwner();
        RunApplyChanges();
    }

    // OWNER

    // The controller may not be awake yet, so the search is tried again over the next three updates. It
    // used to ride the per-frame update for that, which kept every collider on the dispatch until its
    // fourth update: never, for a collider on a switched-off slot, which is dispatched but not updated.
    // 65536 of those in one package cost most of a minute of empty calls. -xlinka
    private void SearchForOwner()
    {
        if (IsDestroyed || _owner != null || Type.Value != ColliderType.CharacterController)
            return;

        foreach (var component in Slot.Components)
        {
            if (component is IColliderOwner owner)
            {
                _owner = owner;
                _owner.OnColliderAdded(this);
                LumoraLogger.Debug($"Collider: found owner {_owner.GetType().Name} on its slot after {_ownerSearches} retries");
                return;
            }
        }

        if (_ownerSearches++ < 3)
            World?.RunInUpdates(1, SearchForOwner);
        else
            LumoraLogger.Warn($"Collider: No IColliderOwner found in same slot '{Slot.SlotName.Value}' for CharacterController collider");
    }

    private bool _watchingMoves;

    // A collider on its rigid body's own slot moves with the body by construction: the body carries the shape,
    // and the body's own hook hears of moves that come from outside it. Listening there anyway queued a notice
    // per body per physics step for nothing. A collider an avatar's pass looks after (AvatarColliderPass) is
    // not listening either.
    private void WatchMoves()
    {
        if (Slot == null)
            return;
        bool watch = !IsDestroyed && !(_body != null && ReferenceEquals(_body.Slot, Slot));
        if (!watch)
        {
            if (_watchingMoves)
                Slot.WorldTransformChanged -= OnSlotTransformChanged;
            _watchingMoves = false;
            LeavePass();
            return;
        }
        if (!_watchingMoves && _passRoot == null)
        {
            _watchingMoves = true;
            Slot.WorldTransformChanged += OnSlotTransformChanged;
        }
    }

    // Applies asked for only because the slot moved. A collider riding a bone of somebody's avatar is asked every
    // frame just to follow it; when every request since the last apply was one of these, the hook only places the
    // body. -xlinka
    public int MoveRequests { get; private set; }

    private void Follow()
    {
        MoveRequests++;
        RunApplyChanges();
    }

    // The avatar this collider sits on, looked up again every 60 frames; set while its pass looks after it.
    private Slot? _passRoot;
    private ulong _rootAt = ulong.MaxValue;
    private Slot? _root;
    private float4x4 _passPose;
    private bool _passPoseValid;

    private Slot? AvatarRoot(ulong frame)
    {
        if (_rootAt == ulong.MaxValue || frame - _rootAt > 60 || frame < _rootAt)
        {
            _root = Avatar.AvatarViewLod.RootOf(Slot);
            _rootAt = frame;
        }
        return _root;
    }

    private void OnSlotTransformChanged(Slot _)
    {
        // A part of a body further down its model: the body re-poses the shape, the collider's own hook has no
        // body to move.
        if (_body != null && !_body.IsDestroyed)
        {
            _body.MarkPartMoved();
            return;
        }
        var world = World;
        if (world != null && Slot != null && AvatarRoot(world.Time?.UpdateIndex ?? 0) is { } root)
        {
            // On an avatar: from here its pass looks after the moves.
            Slot.WorldTransformChanged -= OnSlotTransformChanged;
            _watchingMoves = false;
            _passRoot = root;
            _passPoseValid = false;
            AvatarColliderPass.For(world).Add(this, root);
        }
        Follow();
    }

    // The pass's look on a frame it is due: follow if the world matrix moved since the last follow. False when
    // the collider has left the avatar (it is back on its slot's event) and the pass should drop it.
    internal bool PassLook(ulong frame)
    {
        if (IsDestroyed || Slot == null || _passRoot == null)
        {
            _passRoot = null;
            return false;
        }
        if (!ReferenceEquals(AvatarRoot(frame), _passRoot))
        {
            _passRoot = null;
            WatchMoves();
            Follow();
            return false;
        }
        var pose = Slot.LocalToWorld;
        if (_passPoseValid && pose == _passPose)
            return true;
        _passPose = pose;
        _passPoseValid = true;
        if (_body != null && !_body.IsDestroyed)
            _body.MarkPartMoved();
        else
            Follow();
        return true;
    }

    private void LeavePass()
    {
        if (_passRoot == null)
            return;
        if (World is { } world)
            AvatarColliderPass.For(world).Remove(this, _passRoot);
        _passRoot = null;
    }

    // ABSTRACT METHODS

    // Shape-tight local bounding box, centred on Offset. The platform hook builds the actual collision
    // shape from the typed sync fields, so this is purely the engine-side answer used for culling,
    // fitting and overlap pre-tests.
    public abstract BoundingBox GetLocalBounds();

    private void OnSlotActiveChanged(Slot _) => RunApplyChanges();

    // CLEANUP

    public override void OnDestroy()
    {
        World?.UnregisterCollider(this);

        if (Slot != null)
        {
            if (_watchingMoves)
                Slot.WorldTransformChanged -= OnSlotTransformChanged;
            _watchingMoves = false;
            Slot.ActiveChanged -= OnSlotActiveChanged;
        }
        LeavePass();

        _owner?.OnColliderRemoved(this);
        _owner = null!;
        _body?.MarkCollidersDirty();
        _body = null;

        base.OnDestroy();
        LumoraLogger.Debug($"Collider: Destroyed {GetType().Name}");
    }
}

