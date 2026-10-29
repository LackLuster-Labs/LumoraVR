// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using Lumora.Core.Persistence;
using Lumora.Warden;

namespace Lumora.Core;

public class Slot : ContainerWorker<Component>, IImplementable<IHook<Slot>>, IChangeable, IInitializable, IPermissionGrabSurface
{
    #region Fields

    private readonly List<Slot> _children = new();
    private readonly List<Slot> _localChildren = new();
    // Which of its parent's two lists this slot is filed in, so neither list is walked to find out.
    private Slot? _filedUnder;
    private bool _filedLocal;
    private readonly List<Component> _components = new();

    // Cached read-only views. Each is a LIVE window on its list, so one cached instance per list stays
    // correct as the list changes - and we avoid allocating a fresh wrapper on every access. The UI hit-test
    // and the laser raycast both read Components/Children/LocalChildren per slot, recursively, every frame, so
    // these allocations were a major source of per-frame GC churn (and the resulting stutter). A foreach over
    // one of these takes the list's own enumerator, which AsReadOnly's interface could not. -xlinka
    private ReadOnlyListView<Component>? _componentsReadOnly;
    private ReadOnlyListView<Slot>? _childrenReadOnly;
    private ReadOnlyListView<Slot>? _localChildrenReadOnly;
    private Slot _parent = null!;
    private bool _isRemoved;

    private int _transformDirty = 0;
    private float4x4 _cachedTRS = float4x4.Identity;
    private float4x4 _cachedLocalToWorld = float4x4.Identity;
    private float4x4 _cachedWorldToLocal = float4x4.Identity;
    private float3 _cachedGlobalPosition = float3.Zero;
    private floatQ _cachedGlobalRotation = floatQ.Identity;
    private float3 _cachedGlobalScale = float3.One;

    private const int DIRTY_TRS = 1;
    private const int DIRTY_LOCAL_TO_WORLD = 2;
    private const int DIRTY_WORLD_TO_LOCAL = 4;
    private const int DIRTY_GLOBAL_POSITION = 8;
    private const int DIRTY_GLOBAL_ROTATION = 16;
    private const int DIRTY_GLOBAL_SCALE = 32;
    private const int DIRTY_ALL_GLOBAL = DIRTY_LOCAL_TO_WORLD | DIRTY_WORLD_TO_LOCAL |
                                          DIRTY_GLOBAL_POSITION | DIRTY_GLOBAL_ROTATION | DIRTY_GLOBAL_SCALE;

    private readonly Queue<Action> _scheduledActions = new();
    private readonly object _scheduleLock = new();

    #endregion

    #region Events

    public event Action<IChangeable> Changed = null!;

    public event Action<Slot, Slot> OnChildAdded = null!;

    public event Action<Slot, Slot> OnChildRemoved = null!;

    public event Action<Slot, Component> OnComponentAdded = null!;

    public event Action<Slot, Component> OnComponentRemoved = null!;

    public event Action<Slot, Slot, Slot> OnParentChanged = null!;

    public event Action<Slot, bool> OnActiveChanged = null!;

    public event Action<Slot, string> OnNameChanged = null!;

    // Fired on a slot when one of its children changed places among the others. Adding and removing a
    // child have their own events.
    public event Action<Slot> ChildOrderChanged = null!;

    // Simplified event aliases for UI compatibility
    public event Action<Slot> ActiveChanged = null!;
    public event Action<Slot> ParentChanged = null!;

    public event Action<Slot> PersistentChanged = null!;

    public event Action<Slot> OrderOffsetChanged = null!;

    // Before children/components are torn down.
    public event Action<Slot> OnPrepareDestroy = null!;

    // Fired once per frame (deferred) when this slot's world transform changed - either its own
    // local transform or that of an ancestor. Handlers run before hook/connector updates, so a
    // handler that re-drives a transform takes effect the same frame. Subscribe to react to
    // movement without polling every frame in OnUpdate.
    //
    // A listener's slot starts from a computed world transform and gets one again with every notice. The move
    // cascade stops at a slot whose world transform nobody has computed since it last went stale, and so does
    // everything under it; a listener on a subtree nothing reads (a collider on a bone of a model just
    // imported) was skipped on every later move, and its body stayed where it was first put. Computing it
    // here walks the parents too, so the whole chain down to the listener is open for the next move. -xlinka
    private Action<Slot>? _worldTransformChanged;

    public event Action<Slot> WorldTransformChanged
    {
        add
        {
            _worldTransformChanged += value;
            EnsureValidLocalToWorld();
        }
        remove => _worldTransformChanged -= value;
    }

    // Fired on this slot and on every ancestor of it when a slot or a component is added to or
    // removed from the subtree rooted here. The argument is the slot the change actually happened
    // on, which is a descendant (or this slot itself).
    // Structural only: transforms, field edits and asset swaps never fire it. Anything that caches
    // a measurement of a whole subtree (bounds, counts, budgets) needs to know when the shape of
    // that subtree changed without subscribing to every descendant individually, and re-subscribing
    // on every add/remove costs more than the walk. The bubble is O(depth) with a null-delegate
    // check per level, and it only runs on structural edits, never per frame. -xlinka
    public event Action<Slot> SubtreeStructureChanged = null!;

    // Bubble from the slot that changed up to the root. Ancestors with no listener cost one null
    // check each.
    private void RaiseSubtreeStructureChanged()
    {
        System.Threading.Interlocked.Increment(ref s_structureEpoch);
        var current = this;
        while (current != null)
        {
            current.SubtreeStructureChanged?.Invoke(this);
            current = current._parent;
        }
    }

    #endregion

    #region Sync Fields

    public readonly Sync<string> Name = new();

    [NameOverride("Parent")]
    public readonly SyncRef<Slot> ParentSlotRef = new();

    public readonly Sync<string> Tag = new();

    [NameOverride("Active")]
    public readonly Sync<bool> ActiveSelf = new();

    [NameOverride("Persistent")]
    [NonPersistent]
    public readonly Sync<bool> Persistent = new();

    [NameOverride("Position")]
    public readonly Sync<float3> LocalPosition = new();

    [NameOverride("Rotation")]
    public readonly Sync<floatQ> LocalRotation = new();

    [NameOverride("Scale")]
    public readonly Sync<float3> LocalScale = new();

    [NameOverride("OrderOffset")]
    public readonly Sync<long> OrderOffset = new();

    // PERMISSION GATE VIEW
    // A grab writes the held object's parent and pose, which land on the SLOT, not on the grab component.
    // Only those four members can be grab traffic, and only the ones the grab component on this slot says
    // its grab moves (a carried object all four, a hinge its rotation alone) - anything else here stays
    // owner-gated. The component lookup is deliberately behind the member test so it only runs for a
    // transform write. -xlinka

    // The grab component that answers for this slot: one being held first, else the first switched on.
    // Every question below goes to the same one, so the answers cannot come from two different grabs.
    internal Components.ISlotGrabSurface? GrabSurface
    {
        get
        {
            Components.ISlotGrabSurface? first = null;
            for (int i = 0; i < _components.Count; i++)
            {
                var component = _components[i];
                if (component is not Components.ISlotGrabSurface surface || component.IsDestroyed || !component.Enabled.Value)
                    continue;
                if (surface.CurrentHolder != null)
                    return surface;
                first ??= surface;
            }
            return first;
        }
    }

    bool IPermissionGrabSurface.AllowsGrab => GrabSurface?.AllowsGrab ?? false;

    bool IPermissionGrabSurface.AllowsGrabBy(IPermissionActor actor) => GrabSurface?.AllowsGrabBy(actor) ?? false;

    // Forwarded to the grab component rather than answered here. These used to be hard-coded false/null,
    // which read as "unstealable and held by nobody" for every transform write in the engine. The slot is
    // where the pose and parent writes LAND, but the grab component is where the answer lives. -xlinka
    bool IPermissionGrabSurface.AllowsSteal => GrabSurface?.AllowsSteal ?? false;

    IPermissionActor? IPermissionGrabSurface.CurrentHolder => GrabSurface?.CurrentHolder;

    IPermissionActor? IPermissionGrabSurface.Placer => GrabSurface?.Placer;

    GrabWriteKind IPermissionGrabSurface.ClassifyGrabWrite(IPermissionTarget? member)
    {
        bool transformOrParent =
            ReferenceEquals(member, ParentSlotRef) ||
            ReferenceEquals(member, LocalPosition) ||
            ReferenceEquals(member, LocalRotation) ||
            ReferenceEquals(member, LocalScale);
        return transformOrParent && GrabSurface is { } surface && surface.MovesSlotMember(this, member!)
            ? GrabWriteKind.Transform
            : GrabWriteKind.None;
    }

    public Sync<string> SlotName => Name;

    #endregion

    #region Properties

    public ulong RefIdNumeric => (ulong)ReferenceID;

    public override bool IsPersistent => Persistent.Value;

    // Protected slots refuse Destroy / RemoveFromHierarchy calls. Set via
    // MarkProtected, propagates up the lineage so the whole chain to the
    // protected slot stays alive. Used for world root, user roots, dashboard
    // anchors, anything that should never be destroyed by gameplay code or
    // remote peers. - xlinka
    public bool IsProtected { get; private set; }

    // When true, Persistent flag is treated as forced-on. Set by passing
    // forcePersistent=true to MarkProtected. The Persistent sync field stays
    // writable but consumers should respect this when serializing.
    public bool ForcedPersistent { get; private set; }

    public void MarkProtected(bool forcePersistent = false)
    {
        IsProtected = true;
        if (forcePersistent)
        {
            ForcedPersistent = true;
            if (!Persistent.Value)
                Persistent.Value = true;
        }
        _parent?.MarkProtected(forcePersistent);
    }

    // THE WORLD ROOT
    //
    // The root is the world. Nothing standing inside it may disable it, move it, unparent it, copy it or
    // take it down, and that includes the host: a root that can be switched off or dragged under
    // something is a world that can be bricked from one inspector row, and every peer inherits it.
    //
    // Two halves, because there are two ways at it. The member half marks the pose, the active and
    // persistent flags, the ordering and the parent link host-only (no peer may author them),
    // non-drivable (no drive may either) and config-locked (no local write lands outside an explicit edit
    // scope, which nothing in-world opens for the root). The structural half is MarkProtected, which is
    // what Destroy, DestroyPreservingAssets and RemoveFromHierarchy read.
    //
    // Name and Tag stay writable: renaming the world's root is cosmetic and harmless. -xlinka
    internal void MarkAsWorldRoot()
    {
        MarkProtected();

        LockRootMember(ActiveSelf);
        LockRootMember(Persistent);
        LockRootMember(LocalPosition);
        LockRootMember(LocalRotation);
        LockRootMember(LocalScale);
        LockRootMember(OrderOffset);
        LockRootMember(ParentSlotRef);
    }

    private static void LockRootMember(ConflictingSyncElement member)
    {
        member.MarkHostOnly();
        member.MarkNonDrivable();
        member.MarkConfigLocked();
    }

    // One Warn per refusal reason per slot: a tool that retries (a drag that keeps proposing a reparent)
    // would otherwise write the same line every frame.
    private int _rootRefusalWarned;

    private bool RefuseOnRoot(string operation, int reasonBit)
    {
        if (!IsRootSlot)
            return false;
        if ((_rootRefusalWarned & reasonBit) == 0)
        {
            _rootRefusalWarned |= reasonBit;
            Logging.Logger.Warn($"Slot: refused {operation} on the world root - the world root is fixed for the session.");
        }
        return true;
    }

    private const int RootRefusalDestroy = 1 << 0;
    private const int RootRefusalStrip = 1 << 1;
    private const int RootRefusalDuplicate = 1 << 2;
    private const int RootRefusalReparent = 1 << 3;
    private const int RootRefusalRemove = 1 << 4;

    // Cached nearest-ancestor UserRoot for this slot. Components that need to
    // know "which user does this belong to" should read ActiveUserRoot
    // instead of climbing the hierarchy. UserRoot.OnAwake calls
    // Slot.RegisterUserRoot(this); the slot propagates the reference down to
    // its children. On reparent the inherited value re-resolves from the new
    // parent. Slots that hold their own UserRoot keep that ref and ignore
    // parent propagation. - xlinka
    public UserRoot ActiveUserRoot { get; private set; } = null!;

    public User ActiveUser => (ActiveUserRoot?.ActiveUser) ?? null!;

    public event Action<Slot> ActiveUserRootChanged = null!;

    // When this slot last went into or out of somebody's root (picked up, put down, pinned, worn), in world
    // seconds. A write about it that a peer sent before its machine saw the move lands after it, and the gate
    // does not hold that against the peer for this long. -xlinka
    private double _changedHandsAt = double.NegativeInfinity;

    public const double ChangedHandsWindow = 5.0;

    public bool ChangedHandsRecently => World is { } world && world.TotalTime - _changedHandsAt < ChangedHandsWindow;

    // A slot made where it stands never changed hands: nobody could have written to it from outside first.
    private void SetActiveUserRoot(UserRoot? root)
    {
        ActiveUserRoot = root!;
        if (World is { } world && !IsInInitPhase)
            _changedHandsAt = world.TotalTime;
    }

    internal void RegisterUserRoot(UserRoot userRoot)
    {
        if (userRoot == null || userRoot == ActiveUserRoot)
            return;

        if (ActiveUserRoot != null
            && ActiveUserRoot.Slot != null
            && userRoot.Slot != null
            && ActiveUserRoot.Slot != userRoot.Slot
            && ActiveUserRoot.Slot.IsDescendantOf(userRoot.Slot))
        {
            return;
        }

        SetActiveUserRoot(userRoot);
        PropagateActiveUserRootToChildren();
        ActiveUserRootChanged?.Invoke(this);
    }

    internal void UnregisterUserRootHierarchy(UserRoot userRoot)
    {
        if (userRoot == null || userRoot != ActiveUserRoot)
            return;

        SetActiveUserRoot(null);
        PropagateActiveUserRootToChildren();
        ActiveUserRootChanged?.Invoke(this);
    }

    private void RefreshActiveUserRootFromParent()
    {
        // Slots holding their own UserRoot keep their ref; parent propagation skips them.
        if (ActiveUserRoot != null && ActiveUserRoot.Slot == this)
            return;

        var inherited = _parent?.ActiveUserRoot;
        if (inherited == ActiveUserRoot)
            return;

        SetActiveUserRoot(inherited);
        PropagateActiveUserRootToChildren();
        ActiveUserRootChanged?.Invoke(this);
    }

    private void PropagateActiveUserRootToChildren()
    {
        foreach (var child in _children)
            child.RefreshActiveUserRootFromParent();
        foreach (var child in _localChildren)
            child.RefreshActiveUserRootFromParent();
    }

    public override IEnumerable<IWorldElement> GetReferencedObjects(bool assetRefOnly, bool persistentOnly = true)
    {
        if (ParentSlotRef?.Target != null && (!persistentOnly || ParentSlotRef.Target.IsPersistent))
            yield return ParentSlotRef.Target;

        foreach (var component in _components)
        {
            if (!persistentOnly || component.IsPersistent)
                yield return component;
        }

        foreach (var child in _children)
        {
            if (!persistentOnly || child.IsPersistent)
                yield return child;
        }
    }

    public IHook<Slot> Hook { get; private set; } = null!;

    IHook IImplementable.Hook => Hook;

    Slot IImplementable.Slot => this;

    public override bool IsRemoved => _isRemoved;

    public ReadOnlyListView<Slot> Children => _childrenReadOnly ??= new ReadOnlyListView<Slot>(_children);

    public ReadOnlyListView<Slot> LocalChildren => _localChildrenReadOnly ??= new ReadOnlyListView<Slot>(_localChildren);

    public int ChildCount => _children.Count;

    public int LocalChildCount => _localChildren.Count;

    public new ReadOnlyListView<Component> Components => _componentsReadOnly ??= new ReadOnlyListView<Component>(_components);

    public new int ComponentCount => _components.Count;

    public bool IsRootSlot => World != null && ReferenceEquals(World.RootSlot, this);

    // True if ParentSlotRef has a RefID but the Target hasn't resolved yet. Used to distinguish between true
    // root slots and slots waiting for parent resolution.
    public bool HasPendingParent => _parent == null && ParentSlotRef != null && !ParentSlotRef.Value.IsNull;

    // True if ParentSlotRef hasn't been decoded yet (still in init phase). During network decode, sync members
    // are decoded separately from slots, so we can't know the parent until ParentSlotRef is decoded.
    public bool IsParentUnknown => _parent == null && ParentSlotRef != null && ParentSlotRef.IsInInitPhase;

    // Unlike IsRootSlot, this returns false for slots waiting for parent resolution AND for slots where
    // ParentSlotRef hasn't been decoded yet.
    public bool IsTrueRootSlot => _parent == null &&
        ParentSlotRef != null &&
        !ParentSlotRef.IsInInitPhase &&
        ParentSlotRef.Value.IsNull;

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

    public int SiblingIndex => _parent?._children.IndexOf(this) ?? 0;

    public Slot ObjectRoot
    {
        get
        {
            var current = this;
            while (current != null)
            {
                if (current.GetComponent<ObjectRoot>() != null)
                    return current;
                if (current._parent == null)
                    return current;
                current = current._parent;
            }
            return this;
        }
    }

    public bool IsUnderLocalUser
    {
        get
        {
            var userRoot = ActiveUserRoot;
            return userRoot != null && userRoot.ActiveUser == World?.LocalUser;
        }
    }

    // Cached owning-user lookup lives on ActiveUserRoot. Old GetUserRoot()
    // hierarchy walk was replaced once UserRoot.OnAwake started registering
    // into Slot.RegisterUserRoot. - xlinka
    public UserRoot GetUserRoot() => ActiveUserRoot;

    #endregion

    #region Parent Property

    public new Slot Parent
    {
        get => _parent;
        set => SetParent(value, preserveGlobalTransform: false);
    }

    // Writes ParentSlotRef, which drives the network sync and the internal state update.
    // Warns when the parent changes during the init phase.
    public void SetParent(Slot newParent, bool preserveGlobalTransform = false)
    {
        if (IsRemoved)
        {
            return;
        }

        // The root has nowhere to go. A null target resolves to the root itself further down and is a
        // silent no-op; anything else is somebody trying to hang the world off a prop. -xlinka
        if (IsRootSlot && newParent != null && !ReferenceEquals(newParent, this))
        {
            RefuseOnRoot("reparent", RootRefusalReparent);
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
            newParent = World?.RootSlot!;
        }

        // A slot can never be its own parent, and the descendant check below does NOT catch it (a slot
        // is not its own descendant). Without this the self-reference is WRITTEN to the synced
        // ParentSlotRef and only unwound afterwards, so peers decode a self-parent in between and the
        // slot briefly sits in its own child list - which renders as the same row twice. The world root
        // reaches here through the null-parent fallback above (null resolves to the root, i.e. itself),
        // so this also kills the pointless "tried to assign root slot parent" churn. -xlinka
        if (ReferenceEquals(newParent, this))
        {
            return;
        }

        if (newParent != null && newParent.IsDescendantOf(this))
        {
            return;
        }

        // Nothing is hung on somebody else's body without their yes: the write is to this slot's own parent, so the
        // gate never sees whose body it lands in unless asked here. Your own body may still sit in their seat. -xlinka
        if (newParent != null && !IsInInitPhase && World?.DataModelPermissions is { } gate && !gate.AllowsPlacingUnder(this, newParent))
            return;

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
        ParentSlotRef.Target = newParent!;

        InvalidateGlobalTransforms();

        if (preserveGlobalTransform && newParent != null)
        {
            GlobalPosition = globalPos;
            GlobalRotation = globalRot;
            GlobalScale = globalScale;
        }
    }

    // Guarded reparent for tooling: answers instead of throwing. Every refusal SetParent makes silently
    // (removed slot, self-parent, a parent that is one of our own descendants, the init-phase rule) is
    // decided here first so the caller learns which way it went, and the permission gate is asked BEFORE
    // anything moves - a denial leaves this slot exactly where it was, still attached, never orphaned.
    //
    // A true reparent is one write to the synced parent reference, so there is no half-applied state to
    // unwind: either the reference takes the new value or it keeps the old one. Callers that hand-rolled
    // their own cycle refusal (the glue tool's ancestor walk) should come through here instead. -xlinka
    public bool TrySetParent(Slot newParent, bool preserveGlobalTransform = true)
    {
        if (IsRemoved || IsDestroyed)
            return false;

        newParent ??= World?.RootSlot!;
        if (newParent == null || newParent.IsRemoved || newParent.IsDestroyed)
            return false;

        if (ReferenceEquals(newParent, this) || newParent.IsDescendantOf(this))
            return false;

        if (ReferenceEquals(_parent, newParent))
            return true; // already there, nothing refused

        if (IsInInitPhase && !newParent.IsInInitPhase)
            return false;

        if (!AuthorizeStructuralChange(World, DataModelPermissionAction.ReferenceWrite,
                DataModelPermissionSurface.Slot, this, _parent, throwOnError: false))
        {
            return false;
        }

        try
        {
            SetParent(newParent, preserveGlobalTransform);
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn($"Slot.TrySetParent: '{Name?.Value}' -> '{newParent.Name?.Value}' refused: {ex.Message}");
            return false;
        }

        return ReferenceEquals(_parent, newParent);
    }

    // How many parents up it is from here to root, or -1 when root is not above this slot at all. Depth
    // measures the same thing against the world root; this measures it against any slot you name.
    public int ComputeHierarchyDepth(Slot root)
    {
        int depth = 0;
        var current = this;
        while (current != null && !ReferenceEquals(current, root))
        {
            depth++;
            current = current._parent;
        }
        return current == null ? -1 : depth;
    }

    #endregion

    #region Active State

    // Whether this slot and every slot above it is on, kept until any slot anywhere is switched, moved, loaded
    // or removed. It was a walk to the root on every ask, and every component asks it every frame before it
    // updates while a laser asks it of every target: an open converted graph is a thousand cards fifteen
    // slots deep, so that was a few hundred thousand reads a frame for an answer that almost never moves.
    // -xlinka
    private static int s_activeEpoch;
    private int _activeEpoch = -1;
    private bool _activeCached;

    private static void ActiveStateMoved() => System.Threading.Interlocked.Increment(ref s_activeEpoch);

    internal static int ActiveEpoch => System.Threading.Volatile.Read(ref s_activeEpoch);

    // Moves whenever any slot anywhere goes under another parent or out of the tree: what a cached walk up
    // the tree checks before it trusts what it found.
    private static int s_hierarchyEpoch;

    internal static int HierarchyEpoch => System.Threading.Volatile.Read(ref s_hierarchyEpoch);

    // Moves whenever a component or a slot comes or goes anywhere, or children change order: what a cached
    // search for a component above or below a slot checks before it trusts what it found.
    private static int s_structureEpoch;

    internal static int StructureEpoch => System.Threading.Volatile.Read(ref s_structureEpoch);

    // Moves whenever any slot's name or tag changes: what a cached search by name or tag checks as well.
    private static int s_labelEpoch;

    internal static int LabelEpoch => System.Threading.Volatile.Read(ref s_labelEpoch);

    public bool IsActive
    {
        get
        {
            int epoch = System.Threading.Volatile.Read(ref s_activeEpoch);
            if (_activeEpoch == epoch)
                return _activeCached;
            bool active = ActiveSelf.Value && (_parent?.IsActive ?? true);
            _activeCached = active;
            _activeEpoch = epoch;
            return active;
        }
    }

    public bool IsActiveAndEnabled => IsActive && !IsDestroyed && !_isRemoved;

    // When this slot's ActiveSelf flips, every descendant whose own ActiveSelf is true had its
    // EFFECTIVE active state flip too - fire ActiveChanged on them so effective-active listeners
    // (UI RectTransforms) react. Descendants that are themselves inactive stay inactive regardless,
    // so their subtree is skipped. This is what makes ActiveChanged an EFFECTIVE-active signal.
    private void PropagateActiveChangedToDescendants()
    {
        foreach (var child in Children)
            PropagateActiveChangedToChild(child);
        foreach (var child in LocalChildren)
            PropagateActiveChangedToChild(child);
    }

    private static void PropagateActiveChangedToChild(Slot child)
    {
        if (child == null || child.IsDestroyed || !child.ActiveSelf.Value)
            return;
        child.OnActiveChanged?.Invoke(child, child.IsActive);
        child.ActiveChanged?.Invoke(child);
        child.PropagateActiveChangedToDescendants();
    }

    public void SetActive(bool active, bool recursive = false)
    {
        // The field's own handler announces the flip, to this slot and everything under it.
        ActiveSelf.Value = active;

        if (recursive)
        {
            foreach (var child in _children)
                child.SetActive(active, true);
            foreach (var child in _localChildren)
                child.SetActive(active, true);
        }
    }

    // Drawn on no screen of this machine while set, though the slot stays active: its components run, its
    // colliders collide, and everyone else still sees it. The platform hides the slot's node, which takes
    // everything under it along. Whatever sets it takes it off again; never replicated, never saved. -xlinka
    public bool IsLocallyHidden => _localHides != 0;

    // Who is hiding it. An arriving avatar, a blocked person and a far one drawn as a picture each hide the same
    // slot for their own reasons, and one letting go must not show it while another still wants it gone.
    [System.Flags]
    public enum LocalHide : byte
    {
        Other = 1,
        StandIn = 2,
    }

    private LocalHide _localHides;

    // The platform draws this slot where its simulated body is, between physics steps too, so a position or
    // rotation write leaves the drawn node alone instead of queueing a flush. Whatever sets it clears it and
    // has the node take the slot's pose again. Never replicated, never saved.
    public bool NodePoseDriven { get; set; }

    // Nothing the platform draws hangs off this slot or anything under it: a skeleton bone with no renderer or
    // the like below it, carried to the skin by its skeleton. Its pose writes then queue no flush and its node is
    // left where it was, cut loose from its parent; forty moving avatars pushed over two thousand bone poses a
    // frame to nodes nothing read. Set by the skeleton that owns the bone, and clearing it writes the pose over
    // in full. Never replicated, never saved. -xlinka
    public bool NodePoseUnused
    {
        get => _nodePoseUnused;
        set
        {
            if (_nodePoseUnused == value)
                return;
            _nodePoseUnused = value;
            QueueHookUpdate();
        }
    }

    private volatile bool _nodePoseUnused;

    // Every write of the local pose, counted. Something that writes the pose itself and wants to know whether
    // anything else did since compares this with the count after its own write.
    public int PoseWrites { get; private set; }

    public void SetLocallyHidden(bool hidden) => SetLocallyHidden(LocalHide.Other, hidden);

    public void SetLocallyHidden(LocalHide reason, bool hidden)
    {
        var next = hidden ? _localHides | reason : _localHides & ~reason;
        if (next == _localHides)
            return;
        bool was = _localHides != 0;
        _localHides = next;
        if (was != (next != 0))
            QueueHookUpdate();
    }

    public bool IsLocallyHiddenFor(LocalHide reason) => (_localHides & reason) != 0;

    #endregion

    #region Transform Properties

    public float3 Position
    {
        get => LocalPosition.Value;
        set => LocalPosition.Value = value;
    }

    public floatQ Rotation
    {
        get => LocalRotation.Value;
        set => LocalRotation.Value = value;
    }

    public floatQ Quaternion
    {
        get => LocalRotation.Value;
        set => LocalRotation.Value = value;
    }

    public float3 Scale
    {
        get => LocalScale.Value;
        set => LocalScale.Value = value;
    }

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

    // Where a simulation put this slot, in world space, from the machine simulating it: written like any move
    // (synced, saved, heard by whatever listens), without the checks a hand edit passes on every step of every
    // body. A position or rotation something links still goes through its ordinary setter.
    public void SetSimulatedPose(in float3 position, in floatQ rotation)
    {
        float3 local = position;
        floatQ localRotation = rotation;
        if (_parent != null)
        {
            var parentScale = _parent.GlobalScale;
            var inverse = _parent.GlobalRotation.Inverse;
            var unrotated = inverse * (position - _parent.GlobalPosition);
            local = new float3(
                unrotated.x / (parentScale.x == 0 ? 1 : parentScale.x),
                unrotated.y / (parentScale.y == 0 ? 1 : parentScale.y),
                unrotated.z / (parentScale.z == 0 ? 1 : parentScale.z));
            localRotation = inverse * rotation;
        }
        if (!LocalPosition.SetSimulatedValue(local))
            LocalPosition.Value = local;
        if (!LocalRotation.SetSimulatedValue(localRotation))
            LocalRotation.Value = localRotation;
        if (_parent == null || (_parent._transformDirty & DIRTY_ALL_GLOBAL) == 0)
        {
            _cachedGlobalPosition = position;
            _cachedGlobalRotation = rotation;
            _transformDirty &= ~(DIRTY_GLOBAL_POSITION | DIRTY_GLOBAL_ROTATION);
        }
    }

    // Set GlobalPosition WITHOUT generating a field sync (delta). Use when a stream on this slot is the transport
    // for the position, so it isn't also replicated over the delta channel (mirrors how TrackedDevicePositioner
    // writes stream-shared body nodes). Still fires change events for rendering / transform-dirty / stream
    // sampling - it only skips the sync element going dirty. -xlinka
    public void SetGlobalPositionSilently(float3 value)
    {
        if (_parent == null)
        {
            LocalPosition.SetValueSilently(value, change: true);
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

        LocalPosition.SetValueSilently(new float3(
            unrotated.x / safeScale.x,
            unrotated.y / safeScale.y,
            unrotated.z / safeScale.z
        ), change: true);

        if ((_parent._transformDirty & DIRTY_ALL_GLOBAL) == 0)
        {
            _cachedGlobalPosition = value;
            _transformDirty &= ~DIRTY_GLOBAL_POSITION;
        }
    }

    // Set GlobalRotation WITHOUT generating a field sync (delta). See SetGlobalPositionSilently. -xlinka
    public void SetGlobalRotationSilently(floatQ value)
    {
        if (_parent == null)
        {
            LocalRotation.SetValueSilently(value, change: true);
            _cachedGlobalRotation = value;
            _transformDirty &= ~DIRTY_GLOBAL_ROTATION;
            return;
        }
        LocalRotation.SetValueSilently(_parent.GlobalRotation.Inverse * value, change: true);
        if ((_parent._transformDirty & DIRTY_GLOBAL_ROTATION) == 0)
        {
            _cachedGlobalRotation = value;
            _transformDirty &= ~DIRTY_GLOBAL_ROTATION;
        }
    }

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

    public float4x4 LocalTransform
    {
        get
        {
            EnsureValidTRS();
            return _cachedTRS;
        }
    }

    public float4x4 GlobalTransform
    {
        get
        {
            EnsureValidLocalToWorld();
            return _cachedLocalToWorld;
        }
    }

    public float4x4 LocalToWorld
    {
        get
        {
            EnsureValidLocalToWorld();
            return _cachedLocalToWorld;
        }
    }

    public float4x4 WorldToLocal
    {
        get
        {
            EnsureValidWorldToLocal();
            return _cachedWorldToLocal;
        }
    }

    public float3 Forward => GlobalRotation * float3.Forward;

    public float3 Right => GlobalRotation * float3.Right;

    public float3 Up => GlobalRotation * float3.Up;

    public float3 Backward => GlobalRotation * float3.Backward;

    public float3 Left => GlobalRotation * float3.Left;

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
        // Queue the deferred WorldTransformChanged for any slot that actually has a listener (cheap
        // null check; the queue dedups). Done before the dirty early-return so this slot always
        // registers; descendants register as the cascade visits them.
        if (_worldTransformChanged != null)
            World?.UpdateManager?.RegisterMovedSlot(this);

        if ((_transformDirty & DIRTY_ALL_GLOBAL) == DIRTY_ALL_GLOBAL)
            return;

        _transformDirty |= DIRTY_ALL_GLOBAL;

        foreach (var child in _children)
            child.InvalidateGlobalTransforms();
        foreach (var child in _localChildren)
            child.InvalidateGlobalTransforms();
    }

    internal void FireWorldTransformChanged()
    {
        EnsureValidLocalToWorld();
        _worldTransformChanged?.Invoke(this);
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

    public float3 GlobalPointToLocal(float3 globalPoint)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyPoint(globalPoint);
    }

    public float3 GlobalPointToLocal(in float3 globalPoint)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyPoint(in globalPoint);
    }

    public float3 LocalPointToGlobal(float3 localPoint)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyPoint(localPoint);
    }

    public float3 LocalPointToGlobal(in float3 localPoint)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyPoint(in localPoint);
    }

    public float3 GlobalDirectionToLocal(float3 globalDirection)
    {
        EnsureValidWorldToLocal();
        return _cachedWorldToLocal.MultiplyVector(globalDirection);
    }

    public float3 LocalDirectionToGlobal(float3 localDirection)
    {
        EnsureValidLocalToWorld();
        return _cachedLocalToWorld.MultiplyVector(localDirection);
    }

    public floatQ GlobalRotationToLocal(floatQ globalRotation)
    {
        return GlobalRotation.Inverse * globalRotation;
    }

    public floatQ LocalRotationToGlobal(floatQ localRotation)
    {
        return GlobalRotation * localRotation;
    }

    // ── Vector conversions (rotation + scale, no translation) ───────────────────
    // Like the direction conversions but carry scale; the direction helpers above
    // already use MultiplyVector, so these are the explicitly-named "vector" forms.

    public float3 LocalVectorToGlobal(in float3 localVector)
        => IsRootSlot ? localVector : LocalToWorld.MultiplyVector(in localVector);

    public float3 GlobalVectorToLocal(in float3 globalVector)
        => IsRootSlot ? globalVector : WorldToLocal.MultiplyVector(in globalVector);

    // ── Scale conversions ───────────────────────────────────────────────────────

    public float3 LocalScaleToGlobal(in float3 localScale)
        => IsRootSlot ? localScale : localScale * GlobalScale;

    public float3 GlobalScaleToLocal(in float3 globalScale)
        => IsRootSlot ? globalScale : globalScale / GlobalScale;

    // Averages the components after conversion.
    public float LocalScaleToGlobal(float localScale)
        => IsRootSlot ? localScale : AvgComponent(LocalScaleToGlobal(new float3(localScale, localScale, localScale)));

    // Averages the components after conversion.
    public float GlobalScaleToLocal(float globalScale)
        => IsRootSlot ? globalScale : AvgComponent(GlobalScaleToLocal(new float3(globalScale, globalScale, globalScale)));

    // PARENT-SPACE CONVERSIONS (this slot's local space to and from its parent's space)

    public float3 LocalPointToParent(in float3 localPoint)
        => IsRootSlot ? localPoint : LocalTransform.MultiplyPoint(in localPoint);

    public float3 ParentPointToLocal(in float3 parentPoint)
        => IsRootSlot ? parentPoint : LocalTransform.Inverse.MultiplyPoint(in parentPoint);

    public float3 LocalDirectionToParent(in float3 localDirection)
        => IsRootSlot ? localDirection : LocalRotation.Value * localDirection;

    public float3 ParentDirectionToLocal(in float3 parentDirection)
        => IsRootSlot ? parentDirection : LocalRotation.Value.Inverse * parentDirection;

    public float3 LocalVectorToParent(in float3 localVector)
        => IsRootSlot ? localVector : LocalTransform.MultiplyVector(in localVector);

    public float3 ParentVectorToLocal(in float3 parentVector)
        => IsRootSlot ? parentVector : LocalTransform.Inverse.MultiplyVector(in parentVector);

    public floatQ LocalRotationToParent(in floatQ localRotation)
        => IsRootSlot ? localRotation : LocalRotation.Value * localRotation;

    public floatQ ParentRotationToLocal(in floatQ parentRotation)
        => IsRootSlot ? parentRotation : LocalRotation.Value.Inverse * parentRotation;

    public float3 LocalScaleToParent(in float3 localScale)
        => IsRootSlot ? localScale : LocalScale.Value * localScale;

    public float LocalScaleToParent(float localScale)
        => IsRootSlot ? localScale : AvgComponent(LocalScale.Value * new float3(localScale, localScale, localScale));

    // ARBITRARY-SPACE CONVERSIONS (this slot's local space to and from another slot's)
    // Composed through the cached global/local primitives, with shortcuts when the
    // target space is this slot or its direct parent.

    public float3 LocalPointToSpace(in float3 localPoint, Slot space)
    {
        if (ReferenceEquals(space, this)) return localPoint;
        if (ReferenceEquals(space, Parent)) return LocalPointToParent(in localPoint);
        return space.GlobalPointToLocal(LocalPointToGlobal(in localPoint));
    }

    public float3 SpacePointToLocal(in float3 spacePoint, Slot space)
    {
        if (ReferenceEquals(space, this)) return spacePoint;
        if (ReferenceEquals(space, Parent)) return ParentPointToLocal(in spacePoint);
        return GlobalPointToLocal(space.LocalPointToGlobal(in spacePoint));
    }

    public float3 LocalDirectionToSpace(in float3 localDirection, Slot space)
        => ReferenceEquals(space, this) ? localDirection : space.GlobalDirectionToLocal(LocalDirectionToGlobal(localDirection));

    public float3 SpaceDirectionToLocal(in float3 spaceDirection, Slot space)
        => ReferenceEquals(space, this) ? spaceDirection : GlobalDirectionToLocal(space.LocalDirectionToGlobal(spaceDirection));

    public float3 LocalVectorToSpace(in float3 localVector, Slot space)
        => ReferenceEquals(space, this) ? localVector : space.GlobalVectorToLocal(LocalVectorToGlobal(in localVector));

    public float3 SpaceVectorToLocal(in float3 spaceVector, Slot space)
        => ReferenceEquals(space, this) ? spaceVector : GlobalVectorToLocal(space.LocalVectorToGlobal(in spaceVector));

    public float3 LocalScaleToSpace(in float3 localScale, Slot space)
        => ReferenceEquals(space, this) ? localScale : space.GlobalScaleToLocal(LocalScaleToGlobal(in localScale));

    public float LocalScaleToSpace(float localScale, Slot space)
        => ReferenceEquals(space, this) ? localScale : space.GlobalScaleToLocal(LocalScaleToGlobal(localScale));

    public float3 SpaceScaleToLocal(in float3 spaceScale, Slot space)
        => ReferenceEquals(space, this) ? spaceScale : GlobalScaleToLocal(space.LocalScaleToGlobal(in spaceScale));

    public float SpaceScaleToLocal(float spaceScale, Slot space)
        => ReferenceEquals(space, this) ? spaceScale : GlobalScaleToLocal(space.LocalScaleToGlobal(spaceScale));

    public floatQ LocalRotationToSpace(in floatQ localRotation, Slot space)
        => ReferenceEquals(space, this) ? localRotation : space.GlobalRotationToLocal(LocalRotationToGlobal(localRotation));

    public floatQ SpaceRotationToLocal(in floatQ spaceRotation, Slot space)
        => ReferenceEquals(space, this) ? spaceRotation : GlobalRotationToLocal(space.LocalRotationToGlobal(spaceRotation));

    public float4x4 GetLocalToSpaceMatrix(Slot space)
        => space.WorldToLocal * LocalToWorld;

    private static float AvgComponent(float3 v) => (v.x + v.y + v.z) / 3f;

    public void LookAt(float3 target, float3? up = null)
    {
        var upVec = up ?? float3.Up;
        var direction = (target - GlobalPosition).Normalized;
        if (direction.LengthSquared > 0.0001f)
        {
            GlobalRotation = floatQ.LookRotation(direction, upVec);
        }
    }

    // Euler angles in degrees.
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

    public void SetLocalTransform(float3 position, floatQ rotation, float3 scale)
    {
        LocalPosition.Value = position;
        LocalRotation.Value = rotation;
        LocalScale.Value = scale;
    }

    public void SetGlobalTransform(float3 position, floatQ rotation, float3 scale)
    {
        GlobalPosition = position;
        GlobalRotation = rotation;
        GlobalScale = scale;
    }

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

    public void ResetTransform()
    {
        LocalPosition.Value = float3.Zero;
        LocalRotation.Value = floatQ.Identity;
        LocalScale.Value = float3.One;
    }

    // TRANSFORM BY ANOTHER
    // "Where would this slot end up if the OTHER slot moved to a given pose and dragged this one along?"
    // Take this slot's pose expressed in the other's space right now, then re-plant it against the virtual
    // pose. The other slot is not touched, and this slot does not become its child - only the resulting
    // world pose is handed over (or applied). Placing a grabbed object relative to a snap target and
    // previewing a drag both want exactly this. -xlinka
    //
    // Worked in decomposed TRS rather than through matrices on purpose: this transform model IS a TRS
    // triple, so composing matrices only to decompose them back would lose to shear the moment a
    // non-uniform scale sat under a rotation, and would round-trip every exact value. -xlinka

    public void GetTransformedByAnother(Slot other, in float3 globalPosition, in floatQ globalRotation,
        in float3 globalScale, out float3 position, out floatQ rotation, out float3 scale)
    {
        if (other == null)
        {
            position = GlobalPosition;
            rotation = GlobalRotation;
            scale = GlobalScale;
            return;
        }

        var otherRotation = other.GlobalRotation;
        var otherScale = NonZero(other.GlobalScale);
        var inverseOtherRotation = otherRotation.Inverse;

        var relativePosition = (inverseOtherRotation * (GlobalPosition - other.GlobalPosition)) / otherScale;
        var relativeRotation = inverseOtherRotation * GlobalRotation;
        var relativeScale = GlobalScale / otherScale;

        position = globalPosition + globalRotation * (globalScale * relativePosition);
        rotation = globalRotation * relativeRotation;
        scale = globalScale * relativeScale;
    }

    public float4x4 GetTransformedByAnother(Slot other, in float3 globalPosition, in floatQ globalRotation,
        in float3 globalScale)
    {
        GetTransformedByAnother(other, in globalPosition, in globalRotation, in globalScale,
            out var position, out var rotation, out var scale);
        return float4x4.TRS(position, rotation, scale);
    }

    public void TransformByAnother(Slot other, in float3 globalPosition, in floatQ globalRotation,
        in float3 globalScale)
    {
        if (other == null)
            return;

        GetTransformedByAnother(other, in globalPosition, in globalRotation, in globalScale,
            out var position, out var rotation, out var scale);
        SetGlobalTransform(position, rotation, scale);
    }

    // A zero on any axis makes the relative transform undefined. Substituting 1 keeps a flattened slot
    // from turning the whole result into NaN, which is the failure people actually notice.
    private static float3 NonZero(in float3 value) => new float3(
        value.x == 0f ? 1f : value.x,
        value.y == 0f ? 1f : value.y,
        value.z == 0f ? 1f : value.z);

    #endregion

    #region Constructor & Initialization

    public Slot()
    {
        InitializeSyncFields();

        ComponentAdded += HandleComponentAdded;
        ComponentRemoved += HandleComponentRemoved;
        ParentSlotRef.CheckRemoteSets(AllowsRemoteMove);
    }

    // SPAWN, for a move. Without the Spawn domain a user keeps what they build under their own things,
    // and a slot they made under their root may not be carried out into the world afterwards any more
    // than it could have been made there. Only the sender's own slots are this question; moving anything
    // else is for the gate and the grab rules. A parent in the sender's byte may be new in the same batch,
    // which the registry's own Spawn check follows up. -xlinka
    private bool AllowsRemoteMove(User? sender, RefID claimed, Slot? newParent)
    {
        var world = World;
        if (world == null || sender == null)
            return true;

        // The person boundary, asked of the sender: nothing goes under somebody who has not said yes to them.
        var permissions = world.DataModelPermissions;
        if (newParent != null && permissions != null && !permissions.AllowsPlacingUnder(this, newParent, sender))
            return false;

        byte senderByte = sender.AllocationID.Value;
        if (ReferenceID.GetUserByte() != senderByte)
            return true;

        if (permissions == null || permissions.AllowsDomain(sender, DataModelPermissionDomain.Spawn))
            return true;

        if (claimed.GetUserByte() == senderByte)
            return true;

        Lumora.Warden.IPermissionTarget? target = newParent;
        return target != null && target.IsUnderActorRoot(sender);
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
        ((ISyncMember)componentCollection).Name = "Components";

        Persistent.MarkNonPersistent();

        LocalPosition.OnChanged += _ =>
        {
            PoseWrites++;
            InvalidateTransformCache();
            if (!NodePoseDriven && !_nodePoseUnused)
                QueueHookUpdate();
        };
        LocalRotation.OnChanged += _ =>
        {
            PoseWrites++;
            InvalidateTransformCache();
            if (!NodePoseDriven && !_nodePoseUnused)
                QueueHookUpdate();
        };
        LocalScale.OnChanged += _ =>
        {
            PoseWrites++;
            InvalidateTransformCache();
            if (!_nodePoseUnused)
                QueueHookUpdate();
        };
        ActiveSelf.OnChanged += _ =>
        {
            ActiveStateMoved();
            OnActiveChanged?.Invoke(this, ActiveSelf.Value);
            ActiveChanged?.Invoke(this);
            // Effective active: descendants whose own ActiveSelf is true just had their effective
            // active flip with this ancestor, so fire ActiveChanged on them too (ActiveChanged
            // tracks EFFECTIVE active here, not just ActiveSelf). This is what lets a canvas re-render
            // content shown by reactivating an ancestor (e.g. the dashboard's parked render rig)
            // without any forced-rebuild hack. No listeners other than UI RectTransforms, so safe.
            PropagateActiveChangedToDescendants();
            OnChanged();
        };
        Name.OnChanged += _ =>
        {
            System.Threading.Interlocked.Increment(ref s_labelEpoch);
            OnNameChanged?.Invoke(this, Name.Value);
            OnChanged();
        };
        Tag.OnChanged += static _ => System.Threading.Interlocked.Increment(ref s_labelEpoch);
        Persistent.OnChanged += _ =>
        {
            PersistentChanged?.Invoke(this);
        };
        OrderOffset.OnChanged += _ =>
        {
            _parent?.Reposition(this);
            OrderOffsetChanged?.Invoke(this);
        };

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
        RaiseSubtreeStructureChanged();
        OnChanged();
    }

    private void HandleComponentRemoved(Component component)
    {
        if (component == null)
            return;

        _components.Remove(component);
        World?.UnregisterComponent(component);
        OnComponentRemoved?.Invoke(this, component);
        RaiseSubtreeStructureChanged();
        OnChanged();
    }

    // Used to trigger hook updates when parent info is first decoded from network.
    private void OnParentSlotRefValueChanged(RefID newValue)
    {
        if (Hook != null && !ParentSlotRef.IsInInitPhase)
        {
            QueueHookUpdate();
        }
    }

    // Updates the internal _parent field and child collections. Don't apply null-to-RootSlot fallback during
    // batch decode, as the real parent may not be registered yet.
    private void OnParentSlotRefChanged(SyncRef<Slot> syncRef)
    {
        if (IsDestroyed)
        {
            return;
        }

        if (IsRootSlot)
        {
            // A null here is the root's own shape, and a save writes it out like any other member, so a
            // load reports it back every single time. Nothing is wrong and there is nothing to undo.
            if (syncRef.Target == null)
                return;

            Logging.Logger.Warn($"Tried to assign root slot parent. NewParent={syncRef.Target}");
            // The member is config-locked, so the correction needs the scope the engine's own paths use.
            World?.RunSynchronously(() =>
            {
                using (WorldConfigEdit.Enter())
                    syncRef.Target = null!;
            });
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

        MoveUnder(target);
    }

    // This peer's hierarchy half of a parent change: lists, cached transforms, events, the hook.
    private void MoveUnder(Slot target)
    {
        bool wasActive = IsActive;
        var oldParent = _parent;
        if (_parent != null)
        {
            _parent.DetachChildInternal(this);
        }

        target.AttachChildInternal(this);
        _parent = target;
        ActiveStateMoved();
        System.Threading.Interlocked.Increment(ref s_hierarchyEpoch);

        InvalidateTransformCache();
        RefreshActiveUserRootFromParent();
        OnParentChanged?.Invoke(this, oldParent, target);
        ParentChanged?.Invoke(this);

        // Moved under a parent that is off, or out from under one: the effective state changed with no
        // field of its own moving, and whatever listens for it has to hear. -xlinka
        if (IsActive != wasActive)
        {
            OnActiveChanged?.Invoke(this, IsActive);
            ActiveChanged?.Invoke(this);
            PropagateActiveChangedToDescendants();
        }
        OnChanged();

        if (oldParent == null && Hook != null)
        {
            QueueHookUpdate();
        }
    }

    // Moves this slot under a new parent in this peer's hierarchy only, leaving ParentSlotRef as it is. For
    // a slot whose parent was destroyed out from under it: the authority writes the real move, and this
    // keeps the slot alive and attached until that write lands. The replicated value then names the parent
    // it already has, so it lands as a no-op. -xlinka
    internal void ReattachLocally(Slot newParent)
    {
        if (IsDestroyed || newParent == null || newParent.IsDestroyed || ReferenceEquals(newParent, _parent)
            || ReferenceEquals(newParent, this) || newParent.IsDescendantOf(this))
            return;
        MoveUnder(newParent);
    }

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

    // Initialize this Slot from network replication with a pre-assigned RefID.
    // Used by SlotCollection when creating slots from network data.
    // Runs on the sync thread: no Godot calls.
    // Hook initialization is deferred to the main thread.
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

            // Not RegisterExistingComponents() here.
            // Network-created slots get their components through WorkerCollection network sync,
            // not through pre-attached components. RegisterExistingComponents() is only
            // for locally-created slots that had components attached before Initialize().
            InitializeHook();
        });

        EndInitPhase();
    }

    // Mark all sync members as dirty after slot initialization: sync member values may have
    // been set BEFORE Initialize() was called (e.g., slot.Name.Value = "User X"), when the sync
    // member wasn't yet registered with SyncController, so InvalidateSyncElement() did nothing then.
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
        if (World.HookTypes.GetHookType(typeof(Slot)) == null) return;

        // Inside a load scope the platform node is BUILT LATER, in budgeted bites, while the world is
        // still held pre-Running. Queued in initialize order, which is parent-before-child, so a child's
        // node never looks for a parent node that does not exist yet. -xlinka
        if (World.DeferHookCreation)
        {
            World.QueueHookCreation(CreateHook);
            return;
        }

        CreateHook();
    }

    // A component's hook wants its slot's platform node the moment it starts. Inside a load scope that
    // node is queued rather than built, and in a world that is ALREADY RUNNING the component's startup
    // can reach it before the drain does - so pull it forward, ancestors first, because a node still
    // has to be parented under one that exists. The queued creation then no-ops on the Hook != null
    // guard below. Pure-transform slots (most of a big graph) are untouched and keep spreading. -xlinka
    internal void EnsureHookCreated()
    {
        if (Hook != null || IsDestroyed || _isRemoved || World == null)
            return;
        _parent?.EnsureHookCreated();
        CreateHook();
    }

    private void CreateHook()
    {
        if (IsDestroyed || _isRemoved || World == null || Hook != null)
            return;

        // Deferred/network arrivals are not necessarily ordered by hierarchy. A child must not
        // initialize its platform node before the ancestor hooks it attaches to exist.
        _parent?.EnsureHookCreated();

        Type hookType = World.HookTypes.GetHookType(typeof(Slot));
        if (hookType == null) return;

        Hook = (IHook<Slot>)Activator.CreateInstance(hookType)!;
        Hook?.AssignOwner(this);
        Hook?.Initialize();
        QueueHookUpdate();
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

        bool needsLocalBlock = IsLocalElement && !refController.IsInLocalAllocation;
        using (refController.ElementAllocationScope(RefID.Null, needsLocalBlock))
        {
            var existing = _components.ToArray();
            _components.Clear();

            foreach (var component in existing)
            {
                if (component == null)
                    continue;

                var key = refController.PeekID();
                componentCollection.Add(key, component, isNewlyCreated: true, skipSync: true);
            }
        }
    }

    #endregion

    #region Scheduling

    public override void RunInUpdates(int updateCount, Action action)
    {
        if (action == null) return;

        if (updateCount <= 0)
        {
            action();
            return;
        }

        World?.RunInUpdates(updateCount, action);
    }

    public override void RunSynchronously(Action action)
    {
        if (action == null) return;
        World?.RunSynchronously(action);
    }

    public void RunNextFrame(Action action)
    {
        RunInUpdates(1, action);
    }

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

	public Component AttachComponent(Type componentType)
	{
		if (!typeof(Component).IsAssignableFrom(componentType))
			throw new ArgumentException("Type must derive from Component", nameof(componentType));

        if (World == null)
        {
            var component = (Component)Activator.CreateInstance(componentType)!;
            if (!_components.Contains(component))
            {
                _components.Add(component);
            }
            return component;
        }

        return base.AttachComponent(componentType);
	}

    public T GetOrAttachComponent<T>() where T : Component, new()
    {
        var existing = GetComponent<T>();
        return existing ?? AttachComponent<T>();
    }

    public T GetComponent<T>() where T : Component
    {
        // Hot path: the UI hit-test calls this per slot, twice per laser, every frame. A plain loop avoids
        // the iterator allocation LINQ's OfType<T>().FirstOrDefault() makes on EVERY call - that allocation,
        // multiplied across the whole UI tree each frame, was a big chunk of the UI's per-frame GC churn. -xlinka
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T match)
                return match;
        }
        return null!;
    }

    public Component GetComponent(Type type)
    {
        for (int i = 0; i < _components.Count; i++)
        {
            if (type.IsInstanceOfType(_components[i]))
                return _components[i];
        }
        return null!;
    }

    public IEnumerable<T> GetComponents<T>() where T : Component
    {
        return _components.OfType<T>();
    }

    public IEnumerable<Component> GetAllComponents()
    {
        return _components;
    }

    // Unlike GetComponents, this works with interface types.
    public IEnumerable<T> GetComponentsImplementing<T>() where T : class
    {
        return _components.OfType<T>();
    }

    public bool TryGetComponent<T>(out T component) where T : Component
    {
        component = GetComponent<T>();
        return component != null;
    }

    public T GetComponent<T>(Func<T, bool> predicate) where T : Component
    {
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T match && predicate(match))
                return match;
        }
        return null!;
    }

    public T GetComponentInParent<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            var comp = GetComponent<T>();
            if (comp != null) return comp;
        }

        return (_parent?.GetComponentInParent<T>(true)) ?? null!;
    }

    public T GetComponentInParents<T>(bool includeSelf = true) where T : Component
        => GetComponentInParent<T>(includeSelf);

    // Plain loops over both child lists: the child iterator made an enumerator at every slot the walk passed
    // through, so a search of a user root cost about fifteen kilobytes a call, and the undo history made one a
    // frame. -xlinka
    public T GetComponentInChildren<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            var comp = GetComponent<T>();
            if (comp != null) return comp;
        }

        for (int i = 0; i < _children.Count; i++)
        {
            var comp = _children[i].GetComponentInChildren<T>(true);
            if (comp != null) return comp;
        }
        for (int i = 0; i < _localChildren.Count; i++)
        {
            var comp = _localChildren[i].GetComponentInChildren<T>(true);
            if (comp != null) return comp;
        }

        return null!;
    }

    // One walk with its own stack, depth first in the same order as before. Nested iterators handed every
    // component up through each of its ancestors: on a package of 196612 slots a walk took 600 ms. -xlinka
    public IEnumerable<T> GetComponentsInChildren<T>(bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T match)
                    yield return match;
            }
        }

        var pending = new Stack<Slot>();
        PushChildren(pending, this);
        while (pending.Count > 0)
        {
            var slot = pending.Pop();
            var components = slot._components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is T match)
                    yield return match;
            }
            PushChildren(pending, slot);
        }
    }

    // Shared children before local ones, each in order, for a walk that pops them off the top.
    private static void PushChildren(Stack<Slot> pending, Slot slot)
    {
        for (int i = slot._localChildren.Count - 1; i >= 0; i--)
            pending.Push(slot._localChildren[i]);
        for (int i = slot._children.Count - 1; i >= 0; i--)
            pending.Push(slot._children[i]);
    }

    // Allocation-free variant of GetComponentsInChildren{T}: fills a caller-provided
    // (ideally reused) list instead of allocating an iterator state machine + an OfType wrapper at EVERY
    // slot. The per-frame laser raycast walks the whole world with this, so those allocations were real. -xlinka
    public void GetComponentsInChildren<T>(List<T> results, bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            for (int i = 0; i < _components.Count; i++)
                if (_components[i] is T match) results.Add(match);
        }
        for (int i = 0; i < _children.Count; i++)
            _children[i].GetComponentsInChildren(results, true);
        for (int i = 0; i < _localChildren.Count; i++)
            _localChildren[i].GetComponentsInChildren(results, true);
    }

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

    public T GetComponentInParentsOrChildren<T>() where T : Component
    {
        var comp = GetComponentInParents<T>();
        return comp != null ? comp : GetComponentInChildren<T>(includeSelf: false);
    }

    public T GetComponentInChildrenOrParents<T>() where T : Component
    {
        var comp = GetComponentInChildren<T>();
        return comp != null ? comp : GetComponentInParents<T>(includeSelf: false);
    }

    public void ForeachComponentInParents<T>(Action<T> action, bool includeSelf = true) where T : Component
    {
        foreach (var comp in GetComponentsInParent<T>(includeSelf))
            action(comp);
    }

    // Filtered query overloads (predicate + skip-disabled). The filter is required so the
    // parameterless calls above stay unambiguous; pass null to match any component.
    // "Disabled" = the owning slot is inactive or the component's Enabled flag is false.

    public T GetComponentInChildren<T>(Predicate<T>? filter, bool excludeDisabled = false) where T : Component
    {
        if (excludeDisabled && !IsActive)
            return null!;
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is not T comp) continue;
            if (excludeDisabled && !comp.Enabled.Value) continue;
            if (filter == null || filter(comp)) return comp;
        }
        for (int i = 0; i < _children.Count; i++)
        {
            var found = _children[i].GetComponentInChildren<T>(filter, excludeDisabled);
            if (found != null) return found;
        }
        for (int i = 0; i < _localChildren.Count; i++)
        {
            var found = _localChildren[i].GetComponentInChildren<T>(filter, excludeDisabled);
            if (found != null) return found;
        }
        return null!;
    }

    public IEnumerable<T> GetComponentsInChildren<T>(Predicate<T>? filter, bool excludeDisabled = false) where T : Component
    {
        var pending = new Stack<Slot>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var slot = pending.Pop();
            if (excludeDisabled && !slot.IsActive)
                continue;
            var components = slot._components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is not T comp)
                    continue;
                if (excludeDisabled && !comp.Enabled.Value)
                    continue;
                if (filter == null || filter(comp))
                    yield return comp;
            }
            PushChildren(pending, slot);
        }
    }

    // By index, not OfType: scoped variable lookups walk this up the tree thousands of times a frame, and the
    // iterator pair per level was most of what a busy world's scripts allocated. -xlinka
    public T GetComponentInParents<T>(Predicate<T>? filter, bool excludeDisabled = false) where T : Component
    {
        var current = this;
        while (current != null)
        {
            if (!excludeDisabled || current.IsActive)
            {
                var components = current._components;
                for (int i = 0; i < components.Count; i++)
                {
                    if (components[i] is not T comp) continue;
                    if (excludeDisabled && !comp.Enabled.Value) continue;
                    if (filter == null || filter(comp)) return comp;
                }
            }
            current = current._parent;
        }
        return null!;
    }

    public IEnumerable<T> GetComponentsInParent<T>(Predicate<T>? filter, bool excludeDisabled = false) where T : Component
    {
        var current = this;
        while (current != null)
        {
            if (!excludeDisabled || current.IsActive)
            {
                foreach (var comp in current._components.OfType<T>())
                {
                    if (excludeDisabled && !comp.Enabled.Value) continue;
                    if (filter == null || filter(comp)) yield return comp;
                }
            }
            current = current._parent;
        }
    }

	public new void RemoveComponent(Component component)
	{
        if (component == null)
            return;

        base.RemoveComponent(component);
	}

    public void RemoveComponents<T>() where T : Component
    {
        var toRemove = _components.OfType<T>().ToArray();
        foreach (var comp in toRemove)
            RemoveComponent(comp);
    }

    public bool HasComponent<T>() where T : Component
    {
        return _components.OfType<T>().Any();
    }

    public void ForeachComponent(Action<Component> action)
    {
        foreach (var comp in _components)
            action(comp);
    }

    public void ForeachComponentInChildren<T>(Action<T> action, bool includeSelf = true) where T : Component
    {
        if (includeSelf)
        {
            for (int i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T comp)
                    action(comp);
            }
        }

        for (int i = 0; i < _children.Count; i++)
            _children[i].ForeachComponentInChildren(action, true);
        for (int i = 0; i < _localChildren.Count; i++)
            _localChildren[i].ForeachComponentInChildren(action, true);
    }

    #endregion

    #region Persistence

    // Components and child slots are polymorphic/recursive, so they're serialized explicitly here
    // rather than through the generic member loop; the "Components" member collection is excluded from it.
    // Still chains to base so [NonPersistent] fields (Persistent itself among them) get the same
    // treatment as everywhere else - a slot that made it into a save had Persistent=true already
    // (its parent filters on IsPersistent before calling this), so a loaded slot's constructor default
    // of true is exactly what would have round-tripped anyway.
    protected override bool ShouldSerializeMember(ISyncMember member)
        => !ReferenceEquals(member, componentCollection) && base.ShouldSerializeMember(member);

    public override DataTreeNode Save(SaveControl control)
    {
        var dictionary = (DataTreeDictionary)base.Save(control);

        // Components are type-tagged so the loader knows what to instantiate.
        var componentList = new DataTreeList();
        foreach (var component in Components)
        {
            if (component == null || !control.Writes(component) || control.LeavesOut(component))
                continue;
            componentList.Add(WorkerSaveLoad.SaveWorker(component, control));
        }
        dictionary.Add("Components", componentList);

        // Child slots recurse (always Slot type, so no tag needed).
        var childList = new DataTreeList();
        foreach (var child in Children)
        {
            if (child == null || !child.IsPersistent || control.LeavesOut(child))
                continue;
            childList.Add(child.Save(control));
        }
        dictionary.Add("Children", childList);

        return dictionary;
    }

    public override void Load(DataTreeNode node, LoadControl control)
    {
        if (node is not DataTreeDictionary dictionary)
            return;

        LoadOwn(dictionary, control);
        ActiveStateMoved();

        if (dictionary.TryGetList("Children") is { } childList)
        {
            foreach (var entry in childList.Children)
            {
                var child = AddSlot(string.Empty);
                child.Load(entry, control);
            }
        }
    }

    // The same load a slot at a time: this slot's own members and components now, and its children made in
    // their saved order and handed back to be loaded later, so a big tree can go back over several frames.
    // Siblings take their ids in saved order here as they do above, so they come out in the same order. -xlinka
    internal void LoadShallow(DataTreeNode node, LoadControl control, Queue<(Slot Slot, DataTreeNode Node)> later)
    {
        if (node is not DataTreeDictionary dictionary)
            return;

        LoadOwn(dictionary, control);
        ActiveStateMoved();

        if (dictionary.TryGetList("Children") is { } childList)
        {
            foreach (var entry in childList.Children)
                later.Enqueue((AddSlot(string.Empty), entry));
        }
    }

    private void LoadOwn(DataTreeDictionary dictionary, LoadControl control)
    {
        // Members first (associates this slot's RefID with its saved GUID). The root's pose, flags and
        // ordering are config-locked, so putting a save back into them needs the scope; scoped around the
        // member pass only, so the subtree underneath loads under the ordinary rules. -xlinka
        if (IsRootSlot)
        {
            using (WorldConfigEdit.Enter())
                base.Load(dictionary, control);
        }
        else
        {
            base.Load(dictionary, control);
            // A load sets the order offset without a change event, after the slot already took its place.
            _parent?.Reposition(this);
        }

        if (dictionary.TryGetList("Components") is { } componentList)
        {
            foreach (var entry in componentList.Children)
            {
                var (typeName, data) = WorkerSaveLoad.ExtractWorker(entry, control);
                var type = WorkerManager.GetType(typeName);
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                {
                    Lumora.Core.Logging.Logger.Warn($"Slot.Load: unknown component type '{typeName}', skipping.");
                    continue;
                }
                if (!ComponentExecutionPolicy.CanAttach(type, World))
                {
                    Lumora.Core.Logging.Logger.Warn($"Slot.Load: skipped userspace-only component '{typeName}'.");
                    continue;
                }
                // A world's config components are single-instance and the world root usually already has
                // them by the time a save is read into it (touching World.Configuration attaches one).
                // Loading into the one that is there puts the SAVED settings back, where attaching a second
                // put them on a component nothing reads and the single-instance guard then destroys. -xlinka
                var component = WorldConfigLock.IsConfigComponentType(type) ? GetComponent(type) : null;
                // runOnAttachBehavior: false so the loaded state isn't overwritten by attach defaults.
                component ??= AttachComponent(type, runOnAttachBehavior: false);
                component.Load(data, control);
            }
        }
    }

    // GRAPH SAVE / LOAD - serialize this slot's subtree as a self-contained graph (optionally with
    // its asset dependencies). Lets an object be written to a file/record and loaded into ANY
    // world (spawning, inventory, copy-paste).

    public void GenerateHierarchy(HashSet<Slot> set)
    {
        if (set == null || !set.Add(this))
            return;
        for (int i = 0; i < _children.Count; i++)
            _children[i].GenerateHierarchy(set);
        for (int i = 0; i < _localChildren.Count; i++)
            _localChildren[i].GenerateHierarchy(set);
    }

    // The part of that a save writes: this slot and every child reached through persistent ones, the same
    // walk Save makes. A local or non-persistent child and everything under it never reaches the file.
    public void GenerateSavedHierarchy(HashSet<Slot> set)
    {
        if (set == null || !set.Add(this))
            return;
        for (int i = 0; i < _children.Count; i++)
        {
            if (_children[i].IsPersistent)
                _children[i].GenerateSavedHierarchy(set);
        }
    }

    // DEPENDENCIES
    // What this subtree points AT that does not live inside it. Both walks are transitive - a collected
    // dependency is itself scanned, since an asset can reference another asset and a referenced slot can
    // reference a third. This is the same collection the graph save runs; exposed so duplication, a
    // "would this travel intact?" check, or an inventory pack can ask the question without saving.
    //
    // Two shapes because callers want two different answers. The SLOT walk answers "what else has to come
    // with this for a full deep copy" and returns the outermost slot of each dependency subtree; the ASSET
    // walk answers "what has to come with this to still LOOK right" and returns the individual provider
    // components. References pointing INTO the subtree are not dependencies and are never listed. -xlinka

    public List<Slot> CollectDependencies(bool includeNonPersistent = false)
    {
        var rootHierarchy = new HashSet<Slot>();
        GenerateHierarchy(rootHierarchy);

        var output = new List<Slot>();
        CollectDependencySlots(rootHierarchy, new HashSet<Slot>(), output, includeNonPersistent);
        return output;
    }

    public List<Component> CollectAssetDependencies()
    {
        var savedHierarchy = new HashSet<Slot>();
        GenerateSavedHierarchy(savedHierarchy);

        var output = new List<Component>();
        CollectAssetDependencies(savedHierarchy, new HashSet<Component>(), output);
        return output;
    }

    // References that point outside the subtree (and its collected dependencies) are nulled so the graph stands
    // alone.
    public SavedGraph SaveObject(DependencyHandling dependencyHandling = DependencyHandling.BreakExternal,
                                 bool saveNonPersistent = false, ReferenceTranslator? refTranslator = null)
    {
        if (World == null)
            throw new InvalidOperationException("Cannot SaveObject a slot that isn't in a world.");

        refTranslator ??= new ReferenceTranslator();
        var control = new SaveControl(this, refTranslator) { SaveNonPersistent = saveNonPersistent };

        var rootHierarchy = new HashSet<Slot>();
        GenerateHierarchy(rootHierarchy);

        // CollectAssets brings along referenced asset-provider components; CollectAll brings along
        // every referenced slot (deep copy). dependencyHierarchy = all slots covered by collected
        // slot-dependencies (used for the allow-set + dedup).
        List<Component>? assetDependencies = null;
        List<Slot>? slotDependencies = null;
        var dependencyHierarchy = new HashSet<Slot>();

        if (dependencyHandling == DependencyHandling.CollectAssets)
        {
            assetDependencies = new List<Component>();
            slotDependencies = new List<Slot>();
            var seen = new HashSet<Component>();
            var savedHierarchy = new HashSet<Slot>();
            GenerateSavedHierarchy(savedHierarchy);
            CollectAssetDependencies(savedHierarchy, seen, assetDependencies);
            CollectAssetSlots(this, savedHierarchy, dependencyHierarchy, seen, assetDependencies, slotDependencies, saveNonPersistent);
        }
        else if (dependencyHandling == DependencyHandling.CollectAll)
        {
            slotDependencies = new List<Slot>();
            CollectDependencySlots(rootHierarchy, dependencyHierarchy, slotDependencies, saveNonPersistent);
        }

        // References may target anything inside the subtree or a collected dependency (slots, their
        // components, and all their sync members); everything else is nulled for self-containment.
        var allowed = new HashSet<RefID>();
        foreach (var slot in rootHierarchy)
            AddSlotReferenceableIds(slot, allowed);
        foreach (var slot in dependencyHierarchy)
            AddSlotReferenceableIds(slot, allowed);
        if (assetDependencies != null)
            foreach (var dependency in assetDependencies)
                AddReferenceableIds(dependency, allowed);
        control.ReferenceFilter = r => (r == RefID.Null || allowed.Contains(r)) ? r : RefID.Null;

        // Claim identities for the members this graph points at, after the filter is in place so
        // references leaving the subtree are skipped. Ordering inside the graph then stops mattering:
        // a ref to a field resolves whether its holder was written before or after it.
        control.ReserveSubtreeIdentities(this);
        if (slotDependencies != null)
        {
            foreach (var dependency in slotDependencies)
                control.ReserveSubtreeIdentities(dependency);
        }
        if (assetDependencies != null)
        {
            foreach (var dependency in assetDependencies)
                control.ReserveWorkerIdentities(dependency);
        }

        var dictionary = new DataTreeDictionary();
        dictionary.Add("Object", Save(control));

        if (slotDependencies != null && slotDependencies.Count > 0)
        {
            var dependencyList = new DataTreeList();
            foreach (var dependency in slotDependencies)
                dependencyList.Add(dependency.Save(control));
            dictionary.Add("Dependencies", dependencyList);
        }

        if (assetDependencies != null && assetDependencies.Count > 0)
        {
            control.SaveNonPersistent = true; // dependency components save regardless of slot persistence
            var assetList = new DataTreeList();
            foreach (var dependency in assetDependencies)
                assetList.Add(WorkerSaveLoad.SaveWorker(dependency, control));
            dictionary.Add("Assets", assetList);
        }

        var typeVersions = new DataTreeDictionary();
        control.StoreTypeVersions(typeVersions);
        dictionary.Add("TypeVersions", typeVersions);

        return new SavedGraph(dictionary);
    }

    private static void AddReferenceableIds(Worker worker, HashSet<RefID> set)
    {
        set.Add(worker.ReferenceID);
        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            var member = worker.GetSyncMember(i);
            if (member != null)
                AddReferenceableIds(member, set);
        }
    }

    // A list's elements, and the fields inside them, belong to the save as much as the list does.
    private static void AddReferenceableIds(ISyncMember member, HashSet<RefID> set)
    {
        set.Add(member.ReferenceID);
        foreach (var (_, inner) in InnerMembers(member))
            AddReferenceableIds(inner, set);
    }

    // Gather asset-provider (or PreserveWithAssets) components that what the save writes points at but does not
    // write itself, recursively (assets can reference other assets). Written is the test, not "lives under the
    // root": a material on a non-persistent child sits inside and never reaches the file, and a surface the save
    // writes needs its material however that material is flagged, so either way it travels as an asset. -xlinka
    private static void CollectAssetDependencies(HashSet<Slot> savedHierarchy, HashSet<Component> seen,
                                                 List<Component> output)
    {
        foreach (var slot in savedHierarchy)
            CollectSavedAssetRefs(slot, savedHierarchy, seen, output);
    }

    private static void CollectSavedAssetRefs(Slot slot, HashSet<Slot> savedHierarchy, HashSet<Component> seen,
                                              List<Component> output)
    {
        CollectWorkerAssetRefs(slot, savedHierarchy, seen, output);
        foreach (var component in slot.Components)
        {
            if (component.Persistent)
                CollectWorkerAssetRefs(component, savedHierarchy, seen, output);
        }
    }

    // A slot in the saved hierarchy is written, the save's own root too whatever its flag (SaveControl.Writes),
    // so a component on one is written when its own flag says so.
    private static bool WrittenWith(Component component, HashSet<Slot> savedHierarchy)
        => component.Persistent && component.Slot != null && savedHierarchy.Contains(component.Slot);

    private static void CollectWorkerAssetRefs(Worker worker, HashSet<Slot> savedHierarchy,
        HashSet<Component> seen, List<Component> output)
    {
        // Lists and the entries inside them hold assets too: a renderer's materials, a material's textures.
        var referenced = new List<IWorldElement>();
        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            if (worker.GetSyncMember(i) is { } member)
                referenced.AddRange(CollectMemberReferences(member, assetRefOnly: true, persistentOnly: false));
        }

        foreach (var element in referenced)
        {
            if (element is not Component target || target.IsDestroyed)
                continue;
            if (target is not IAssetProvider && !target.PreserveWithAssets)
                continue;
            if (WrittenWith(target, savedHierarchy))
                continue;
            if (!seen.Add(target))
                continue;
            output.Add(target);
            CollectWorkerAssetRefs(target, savedHierarchy, seen, output);
        }
    }

    // A collected asset can need slots of its own to mean anything: a shader graph's material is made from the
    // nodes of its graph. Those slots travel whole beside the object as its dependencies, with whatever assets
    // they point at in turn. Never a slot the saved object already holds or hangs under. -xlinka
    private static void CollectAssetSlots(Slot root, HashSet<Slot> savedHierarchy, HashSet<Slot> dependencyHierarchy,
        HashSet<Component> seen, List<Component> assets, List<Slot> output, bool saveNonPersistent)
    {
        var inside = new HashSet<Slot>(savedHierarchy);
        for (int i = 0; i < assets.Count; i++)
        {
            if (assets[i] is not IAssetSlotDependencies carrier)
                continue;
            foreach (var slot in carrier.SlotDependencies)
            {
                if (slot == null || slot.IsDestroyed || inside.Contains(slot) || root.IsDescendantOf(slot))
                    continue;
                if (!saveNonPersistent && !slot.IsPersistent)
                    continue;

                var subtree = new HashSet<Slot>();
                slot.GenerateSavedHierarchy(subtree);
                output.RemoveAll(existing => subtree.Contains(existing));
                output.Add(slot);
                inside.UnionWith(subtree);
                dependencyHierarchy.UnionWith(subtree);
                foreach (var carried in subtree)
                    CollectSavedAssetRefs(carried, inside, seen, assets);
            }
        }

        // One that lives in a carried slot is saved with that slot, not a second time on its own.
        assets.RemoveAll(asset => WrittenWith(asset, dependencyHierarchy));
    }

    private static void AddSlotReferenceableIds(Slot slot, HashSet<RefID> set)
    {
        AddReferenceableIds(slot, set);
        foreach (var component in slot.Components)
            AddReferenceableIds(component, set);
    }

    // Gather every slot referenced from inside the subtree but living outside it, recursively
    // (each collected dependency subtree is itself scanned for further external references).
    private static void CollectDependencySlots(HashSet<Slot> rootHierarchy, HashSet<Slot> dependencyHierarchy,
                                               List<Slot> output, bool saveNonPersistent)
    {
        var toScan = new Queue<Slot>(rootHierarchy);
        while (toScan.Count > 0)
        {
            var slot = toScan.Dequeue();
            ScanForDependencySlots(slot, rootHierarchy, dependencyHierarchy, output, toScan, saveNonPersistent);
            foreach (var component in slot.Components)
                ScanForDependencySlots(component, rootHierarchy, dependencyHierarchy, output, toScan, saveNonPersistent);
        }
    }

    private static void ScanForDependencySlots(Worker worker, HashSet<Slot> rootHierarchy,
        HashSet<Slot> dependencyHierarchy, List<Slot> output, Queue<Slot> toScan, bool saveNonPersistent)
    {
        // A slot's own parent is not something it DEPENDS on - it is where it currently happens to hang,
        // and a loaded graph gets re-parented by whoever loads it. Following it would make the subtree
        // root's parent a dependency, and since a dependency drags its whole subtree along, one slot's
        // parent ref would pull in the entire world. -xlinka
        var parentRef = (worker as Slot)?.ParentSlotRef;

        // Lists and the entries inside them hold references too: a LOD level's renderers, a script's bindings.
        var targets = new List<IWorldElement>();
        for (int i = 0; i < worker.SyncMemberCount; i++)
        {
            var member = worker.GetSyncMember(i);
            if (member != null && (parentRef == null || !ReferenceEquals(member, parentRef)))
                targets.AddRange(CollectMemberReferences(member, assetRefOnly: false, persistentOnly: false));
        }

        foreach (var target in targets)
        {
            if (target == null || target.IsDestroyed)
                continue;
            var slot = target as Slot ?? (target as Component)?.Slot;
            if (slot == null || slot.IsDestroyed)
                continue;
            if (rootHierarchy.Contains(slot) || dependencyHierarchy.Contains(slot))
                continue;
            if (!saveNonPersistent && !slot.IsPersistent)
                continue;

            // New top-level dependency: save the whole subtree it roots, drop any earlier-collected
            // root that turns out to live inside it, and queue its (new) slots for further scanning.
            var subtree = new HashSet<Slot>();
            slot.GenerateHierarchy(subtree);
            output.RemoveAll(existing => subtree.Contains(existing));
            output.Add(slot);
            foreach (var s in subtree)
                if (dependencyHierarchy.Add(s))
                    toScan.Enqueue(s);
        }
    }

    // Any collected "Assets" are attached under assetsRoot (or the object's own holder, ObjectAssetHolders) so
    // their references resolve. Intended for an empty target slot (e.g. parent.AddSlot().LoadObject(…)).
    public void LoadObject(DataTreeDictionary node, Slot? assetsRoot = null, ReferenceTranslator? refTranslator = null)
    {
        if (IsDestroyed)
            throw new InvalidOperationException("Cannot LoadObject onto a destroyed slot.");
        if (World == null)
            throw new InvalidOperationException("Cannot LoadObject onto a slot that isn't in a world.");

        refTranslator ??= new ReferenceTranslator();
        var control = new LoadControl(World, refTranslator);

        // A big item lands as one frame of platform-node construction, which is the spawn spike. Past the
        // threshold the nodes get COLLECTED instead and the world builds them in budgeted bites from its
        // own update, so a full room drops in over a few frames instead of one stall. Small spawns are
        // left strictly alone: a prop is fully built by the time this returns, which is what every
        // spawn-then-configure caller in the codebase assumes. -xlinka
        bool deferred = World.State == World.WorldState.Running
            && CountSavedSlots(node) >= DeferredSpawnSlotThreshold;
        if (deferred)
            World.BeginDeferredHookCreation();

        try
        {
            if (node.TryGetDictionary("TypeVersions") is { } typeVersions)
                control.LoadTypeVersions(typeVersions);

            // The saved root's own parent reference was nulled by the filter; remember where this
            // slot lives so loading the (parentless) object doesn't detach it from the hierarchy.
            var originalParent = _parent;

            if (node.TryGetNode("Object") is { } objectNode)
                Load(WithoutCutParent(objectNode), control);

            if (originalParent != null && !ReferenceEquals(_parent, originalParent))
                SetParent(originalParent, preserveGlobalTransform: false);

            if (node.TryGetList("Dependencies") is { } dependencyList && dependencyList.Children.Count > 0)
            {
                var dependencyHost = ObjectAssetHolders.For(this, ObjectAssetHolders.DependenciesName);
                foreach (var entry in dependencyList.Children)
                    dependencyHost.AddSlot(string.Empty).Load(WithoutCutParent(entry), control);
            }

            if (node.TryGetList("Assets") is { } assetList && assetList.Children.Count > 0)
                (assetsRoot ?? ObjectAssetHolders.For(this, ObjectAssetHolders.AssetsName)).AttachSavedComponents(assetList, control);
        }
        finally
        {
            control.FinishLoad();
            if (deferred)
                World.EndDeferredHookCreation();
        }
    }

    // THE SAME LOAD OVER SEVERAL FRAMES.
    //
    // A big object taken out of an inventory went into the world in one call, every slot, component and value:
    // a fifth of a second of one frame for an avatar, and more for a room. This puts it back a slot at a time, a
    // few milliseconds of each frame, the way a saved world goes back. Nothing in it starts until the last slot
    // is in: started on half a tree, a skeleton watched bones that were not there yet and went over them again
    // as each one arrived. The object stays switched on throughout, since switching a whole avatar on at the end
    // was a half-second frame of its own; each bite's platform nodes are made afterwards in budgeted bites, as
    // for any big spawn, and once the object is whole its components start a budgeted few a frame.
    //
    // Small objects, or a world not running, load in one call as before. scope is entered around every bite
    // (whose allocation it is). whenStarted hears this slot once its own values are in, which is when the caller
    // may move it; whenDone hears it once the whole object is, or null when the load failed or the slot went
    // away meanwhile. -xlinka
    public void LoadObjectStaged(DataTreeDictionary node, Func<IDisposable?>? scope, Action<Slot>? whenStarted, Action<Slot?>? whenDone)
    {
        if (IsDestroyed)
            throw new InvalidOperationException("Cannot LoadObject onto a destroyed slot.");
        var world = World ?? throw new InvalidOperationException("Cannot LoadObject onto a slot that isn't in a world.");
        if (world.State != World.WorldState.Running || CountSavedSlots(node) < DeferredSpawnSlotThreshold)
        {
            using (scope?.Invoke())
                LoadObject(node);
            whenStarted?.Invoke(this);
            whenDone?.Invoke(this);
            return;
        }
        new StagedObjectLoad(this, world, node, scope, whenStarted, whenDone).Bite();
    }

    private sealed class StagedObjectLoad
    {
        // A bite always gets this many slots in, so a load moves on in a frame that is already over.
        private const int MinSlotsPerBite = 16;

        private readonly Slot _root;
        private readonly World _world;
        private readonly DataTreeDictionary _node;
        private readonly Func<IDisposable?>? _scope;
        private readonly Action<Slot>? _whenStarted;
        private readonly Action<Slot?>? _whenDone;
        private readonly LoadControl _control;
        private readonly Queue<(Slot Slot, DataTreeNode Node)> _later = new();
        private readonly List<IUpdatable> _held = new();
        private readonly Action _next;
        private bool _begun;
        private bool _treeDone;
        private int _assetIndex;
        private Slot? _assetsHost;

        public StagedObjectLoad(Slot root, World world, DataTreeDictionary node, Func<IDisposable?>? scope, Action<Slot>? whenStarted, Action<Slot?>? whenDone)
        {
            _root = root;
            _world = world;
            _node = node;
            _scope = scope;
            _whenStarted = whenStarted;
            _whenDone = whenDone;
            _control = new LoadControl(world, new ReferenceTranslator());
            _next = Bite;
        }

        public void Bite()
        {
            if (_world.IsDisposed)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long until = started + World.StagedBudgetTicks;
            bool first = !_begun;
            bool done = false;
            bool failed = false;
            var updates = _world.UpdateManager;
            _world.BeginDeferredHookCreation();
            updates?.HoldStartups(_held);
            try
            {
                using (_scope?.Invoke())
                {
                    if (first)
                    {
                        _begun = true;
                        var originalParent = _root._parent;
                        if (_node.TryGetDictionary("TypeVersions") is { } typeVersions)
                            _control.LoadTypeVersions(typeVersions);
                        if (_node.TryGetNode("Object") is { } objectNode)
                            _root.LoadShallow(_root.WithoutCutParent(objectNode), _control, _later);
                        if (originalParent != null && !ReferenceEquals(_root._parent, originalParent))
                            _root.SetParent(originalParent, preserveGlobalTransform: false);
                    }

                    int ran = 0;
                    while (_later.Count > 0 && !_root.IsDestroyed)
                    {
                        var (slot, child) = _later.Dequeue();
                        if (!slot.IsDestroyed)
                            slot.LoadShallow(child, _control, _later);
                        if (++ran >= MinSlotsPerBite
                            && (System.Diagnostics.Stopwatch.GetTimestamp() >= until || Engine.LoadAllowanceSpent(started, Engine.LoadShareAll)))
                            break;
                    }

                    if ((_later.Count == 0 || _root.IsDestroyed) && !_treeDone)
                    {
                        _treeDone = true;
                        if (!_root.IsDestroyed && _node.TryGetList("Dependencies") is { } dependencyList && dependencyList.Children.Count > 0)
                        {
                            var dependencyHost = ObjectAssetHolders.For(_root, ObjectAssetHolders.DependenciesName);
                            foreach (var entry in dependencyList.Children)
                                dependencyHost.AddSlot(string.Empty).Load(_root.WithoutCutParent(entry), _control);
                        }
                    }

                    // A room carries its materials, meshes and pictures beside the tree, thousands of them: they go
                    // in a bite at a time as well, after the last slot. -xlinka
                    if (_treeDone && !_root.IsDestroyed && _node.TryGetList("Assets") is { } assetList && _assetIndex < assetList.Children.Count
                        && System.Diagnostics.Stopwatch.GetTimestamp() < until)
                    {
                        _assetsHost ??= ObjectAssetHolders.For(_root, ObjectAssetHolders.AssetsName);
                        _assetIndex = _assetsHost.AttachSavedComponents(assetList, _control, _assetIndex, until);
                    }

                    if (_treeDone && (_root.IsDestroyed || _node.TryGetList("Assets") is not { } assets || _assetIndex >= assets.Children.Count))
                    {
                        done = true;
                        _control.FinishLoad();
                    }
                }
            }
            catch (Exception ex)
            {
                Lumora.Core.Logging.Logger.Error($"Slot.LoadObjectStaged: loading '{_root.Name}' failed: {ex.Message}");
                failed = true;
                if (!done)
                {
                    done = true;
                    try { _control.FinishLoad(); } catch (Exception) { }
                }
            }
            finally
            {
                updates?.EndStartupHold();
                _world.EndDeferredHookCreation();
                Engine.ChargeLoad(started);
            }

            if (first && !failed && !_root.IsDestroyed)
                _whenStarted?.Invoke(_root);
            if (!done)
            {
                _world.RunOnNextUpdate(_next);
                return;
            }
            updates?.ReleaseStartups(_held);
            _whenDone?.Invoke(failed || _root.IsDestroyed ? null : _root);
        }
    }

    // Components saved on their own rather than as part of a slot: the assets a graph or a world carried beside
    // its tree. Each is attached here and loaded.
    internal void AttachSavedComponents(DataTreeList list, LoadControl control)
        => AttachSavedComponents(list, control, 0, long.MaxValue);

    // From entry start on, until the clock passes until (one entry at the least); the index to go on from.
    internal int AttachSavedComponents(DataTreeList list, LoadControl control, int start, long until)
    {
        var entries = list.Children;
        int i = start;
        while (i < entries.Count)
        {
            var entry = entries[i++];
            if (i - start > 1 && System.Diagnostics.Stopwatch.GetTimestamp() >= until)
            {
                i--;
                break;
            }
            var (typeName, data) = WorkerSaveLoad.ExtractWorker(entry, control);
            var type = WorkerManager.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                Lumora.Core.Logging.Logger.Warn($"Slot.AttachSavedComponents: unknown asset component type '{typeName}', skipping.");
                continue;
            }
            if (!ComponentExecutionPolicy.CanAttach(type, World))
            {
                Lumora.Core.Logging.Logger.Warn($"Slot.AttachSavedComponents: skipped userspace-only asset '{typeName}'.");
                continue;
            }
            var component = AttachComponent(type, runOnAttachBehavior: false);
            component.Load(data, control);
        }
        return i;
    }

    // A slot written away from where it hangs, its parent not in the save: the parent reference is left out
    // rather than written to resolve to nothing, and whoever loads it decides where it goes.
    internal DataTreeNode SaveDetached(SaveControl control)
    {
        var node = Save(control);
        if (node is DataTreeDictionary dictionary)
            dictionary.Remove(GetSyncMemberName(IndexOfMember(ParentSlotRef)));
        return node;
    }

    // The saved object's parent, and each dependency's, lay outside the save and were cut. Loading that empty
    // reference hands the slot to the world root a frame later, so it is left out and the slot stays where
    // the load put it: the object where its caller made it, a dependency under the dependencies holder. -xlinka
    private DataTreeNode WithoutCutParent(DataTreeNode slotNode)
    {
        var key = GetSyncMemberName(IndexOfMember(ParentSlotRef));
        if (slotNode is not DataTreeDictionary dictionary || dictionary.TryGetNode(key) is not DataTreeValue { IsNull: true })
            return slotNode;
        var trimmed = new DataTreeDictionary();
        foreach (var (name, child) in dictionary.Children)
        {
            if (name != key)
                trimmed.Add(name, child);
        }
        return trimmed;
    }

    // Slots big enough that building every platform node inline is a visible hitch. Tuned to sit well
    // above anything the spawn-inline paths (and their probes) rely on and well below a real prop. -xlinka
    public const int DeferredSpawnSlotThreshold = 96;

    // Counts the slots a saved graph will produce, without loading it: the object node plus every
    // descendant in its Children lists. Cheap - it walks the parsed tree, it does not build anything.
    public static int CountSavedSlots(DataTreeDictionary node)
    {
        var objectNode = node.TryGetNode("Object") as DataTreeDictionary;
        int total = objectNode != null ? CountSlotSubtree(objectNode) : 0;

        if (node.TryGetList("Dependencies") is { } dependencies)
        {
            foreach (var entry in dependencies.Children)
            {
                if (entry is DataTreeDictionary dependency)
                    total += CountSlotSubtree(dependency);
            }
        }
        return total;
    }

    private static int CountSlotSubtree(DataTreeDictionary slotNode)
    {
        int total = 1;
        if (slotNode.TryGetList("Children") is { } children)
        {
            foreach (var child in children.Children)
            {
                if (child is DataTreeDictionary childDictionary)
                    total += CountSlotSubtree(childDictionary);
            }
        }
        return total;
    }

    // Set encrypt to store it AES-GCM encrypted at rest (inventory items / saved objects).
    public void SaveObjectToFile(string path, DependencyHandling dependencyHandling = DependencyHandling.CollectAssets,
                                 bool saveNonPersistent = false, bool encrypt = false)
    {
        if (Security.ItemSeals.Refuses(this, Security.SealRule.Export, out var refusal))
            throw new UnauthorizedAccessException($"'{Name.Value}' cannot be written to a file: {refusal}");
        var graph = SaveObject(dependencyHandling, saveNonPersistent);
        var bytes = graph.SaveToBytes();
        if (encrypt)
            bytes = LocalEncryption.Encrypt(bytes);
        File.WriteAllBytes(path, bytes);
    }

    public void LoadObjectFromFile(string path, Slot? assetsRoot = null, ReferenceTranslator? refTranslator = null)
    {
        if (DataTreeConverter.LoadFromBytes(LocalEncryption.Decrypt(File.ReadAllBytes(path))) is DataTreeDictionary dictionary)
            LoadObject(dictionary, assetsRoot, refTranslator);
        else
            throw new InvalidDataException("File does not contain a saved object graph.");
    }

    // Asynchronously load a graph from a local file: the bytes are read and decrypted off-thread, then the
    // load itself runs on the world update thread (data-model mutations must happen there). Awaitable from
    // anywhere - it opens its own context rather than needing a world task around it.
    //
    // This slot owns the context, so there is no destroyed check on the far side: a slot deleted while the
    // file is still being read cancels the load on the way back instead of loading into a corpse. The
    // caller sees that as a cancelled task rather than a silent no-op. -xlinka
    public async Task LoadObjectAsync(string path, Slot? assetsRoot = null, ReferenceTranslator? refTranslator = null)
    {
        WorldContext.Enter(World, this);
        await WorldContext.ToBackground();

        var bytes = await File.ReadAllBytesAsync(path);
        var node = DataTreeConverter.LoadFromBytes(LocalEncryption.Decrypt(bytes));

        await WorldContext.ToWorld();
        if (node is DataTreeDictionary dictionary)
            LoadObject(dictionary, assetsRoot, refTranslator);
    }

    #endregion

    #region Child Slot Methods

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

        // A new child under somebody else's body is a change to it, the same as a reparent into it.
        if (World.DataModelPermissions is { } gate && !gate.AllowsPlacingUnder(null, this))
            throw new UnauthorizedAccessException("another person's body and what they hold are theirs to change; ask them first");

        using var inherited = refController.InheritedAllocationScope(ReferenceID);
        var slot = new Slot();
        slot.Name.Value = name;
        slot.Parent = this;
        slot.Initialize(World);
        World.RegisterSlot(slot);

        return slot;
    }

    // Not synchronized.
    public Slot AddLocalSlot(string name = "LocalSlot")
    {
        if (World == null)
            return AddSlot(name);

        var refController = World.ReferenceController;
        if (refController == null)
        {
            var unscoped = new Slot();
            unscoped.Name.Value = name;
            unscoped.Parent = this;
            unscoped.Initialize(World);
            World.RegisterSlot(unscoped);
            return unscoped;
        }

        // Initialize runs the slot's members and hook, and RegisterSlot runs the world's slot-added
        // listeners; a throw out of either used to skip the end of the block. -xlinka
        using (refController.LocalAllocationScope())
        {
            var slot = new Slot();
            slot.Name.Value = name;
            slot.Parent = this;
            slot.Initialize(World);
            World.RegisterSlot(slot);
            return slot;
        }
    }

    private bool ShouldStoreInLocalChildren(Slot child)
    {
        return child != null && child.IsLocalElement && !IsLocalElement;
    }

    private void AttachChildInternal(Slot child)
    {
        if (child == null)
            return;

        // A locally-allocated child gets its local RefID only during Initialize, AFTER the first
        // parent attach already filed it under _children. Re-file on the post-init attach so a
        // local slot lives in exactly one list; otherwise it double-registers and leaks into
        // Children (and from there into saves, duplication and the inspector tree). - xlinka
        var list = ShouldStoreInLocalChildren(child) ? _localChildren : _children;
        bool local = !ReferenceEquals(list, _children);
        // The child says where it is filed. Walking both lists to find out made every add under a slot
        // slower than the last: 65536 children under one slot took 7 s. -xlinka
        bool filedHere = ReferenceEquals(child._filedUnder, this);
        bool refiled = filedHere && child._filedLocal != local
            && (child._filedLocal ? _localChildren.Remove(child) : RemoveOrdered(child));

        if (!filedHere || refiled)
        {
            if (local)
                list.Add(child);
            else
                _children.Insert(OrderedIndex(child), child);
            child._filedUnder = this;
            child._filedLocal = local;
            if (IsInInitPhase && !refiled)
            {
                childInitializables.Add(child);
            }
            if (!refiled)
            {
                OnChildAdded?.Invoke(this, child);
                RaiseSubtreeStructureChanged();
            }
        }
        else if (!local)
        {
            // The attach Initialize repeats. The child was filed before it had a RefID and has one now.
            Reposition(child);
        }
    }

    // ORDER
    //
    // The shared child list is kept sorted by order offset, then RefID, both replicated, so every machine
    // lists a slot's children the same way whatever order it heard about them in. A new slot takes offset 0
    // and the newest RefID of its author, so things made one after another stay in the order they were
    // made. A move writes offsets, which is how it reaches everyone. Local children of a shared slot are
    // nobody else's business and stay in the order they came. -xlinka

    // Room left on either side of a written offset, so the next move usually finds space without
    // renumbering anyone else.
    private const long OrderStep = 100;

    private static int CompareOrder(Slot a, Slot b)
    {
        int byOffset = a.OrderOffset.Value.CompareTo(b.OrderOffset.Value);
        return byOffset != 0 ? byOffset : OrderId(a).CompareTo(OrderId(b));
    }

    // A slot filed before it has a RefID sorts as the newest.
    private static ulong OrderId(Slot slot) => slot.ReferenceID.IsNull ? ulong.MaxValue : slot.ReferenceID.RawValue;

    // Where child belongs in the sorted list, after anything that sorts equal.
    private int OrderedIndex(Slot child)
    {
        int low = 0;
        int high = _children.Count;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (CompareOrder(_children[middle], child) <= 0)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    // A child's own place in the sorted list, found by its order key; a walk only if the key moved under it.
    private bool RemoveOrdered(Slot child)
    {
        int at = OrderedIndex(child) - 1;
        if (at >= 0 && ReferenceEquals(_children[at], child))
        {
            _children.RemoveAt(at);
            return true;
        }
        return _children.Remove(child);
    }

    // Puts a child whose order key changed back where it belongs.
    private void Reposition(Slot child)
    {
        int at = _children.LastIndexOf(child);
        if (at < 0)
            return;
        if ((at == 0 || CompareOrder(_children[at - 1], child) <= 0)
            && (at == _children.Count - 1 || CompareOrder(child, _children[at + 1]) <= 0))
            return;
        _children.RemoveAt(at);
        _children.Insert(OrderedIndex(child), child);
        System.Threading.Interlocked.Increment(ref s_structureEpoch);
        ChildOrderChanged?.Invoke(this);
    }

    private void DetachChildInternal(Slot child)
    {
        if (child == null)
            return;

        bool removed;
        if (ReferenceEquals(child._filedUnder, this))
        {
            // A slot being destroyed empties its lists in one go once its children are gone; taking each
            // out of the front first moved the rest of the list every time.
            removed = IsDestroyed || (child._filedLocal ? _localChildren.Remove(child) : RemoveOrdered(child));
            child._filedUnder = null;
        }
        else
        {
            // Check both lists: the child's local flag can differ between attach and detach. - xlinka
            removed = _children.Remove(child);
            removed |= _localChildren.Remove(child);
        }
        if (removed)
        {
            OnChildRemoved?.Invoke(this, child);
            RaiseSubtreeStructureChanged();
        }
    }

    private IEnumerable<Slot> EnumerateAllChildren()
    {
        foreach (var child in _children)
            yield return child;
        foreach (var child in _localChildren)
            yield return child;
    }

    public Slot GetChild(int index)
    {
        if (index < 0 || index >= _children.Count)
            return null!;
        return _children[index];
    }

    public Slot this[int index] => GetChild(index);

    public Slot this[string name] => GetChild(name);

    public Slot GetChild(string name)
    {
        return _children.FirstOrDefault(c => c.Name.Value == name)!;
    }

    public Slot FindChild(string name, bool recursive = false, int maxDepth = -1)
    {
        // One level down by name is most of the asks, several a frame for some of them; it needs neither a
        // predicate nor an enumerator. Same order as the walk below: children, then local children. -xlinka
        if (!recursive)
        {
            foreach (var child in _children)
            {
                if (child.Name.Value == name)
                    return child;
            }
            foreach (var child in _localChildren)
            {
                if (child.Name.Value == name)
                    return child;
            }
            return null!;
        }
        return FindChild(s => s.Name.Value == name, recursive, maxDepth);
    }

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
        return null!;
    }

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

    public IEnumerable<Slot> FindChildrenByTag(string tag, bool recursive = false)
    {
        return FindChildren(s => s.Tag.Value == tag, recursive);
    }

    public void TagHierarchy(string tag)
    {
        Tag.Value = tag;
        foreach (var child in EnumerateAllChildren())
            child.TagHierarchy(tag);
    }

    public List<Slot> GetChildrenWithTag(string tag)
    {
        var result = new List<Slot>();
        GetChildrenWithTag(tag, result);
        return result;
    }

    public void GetChildrenWithTag(string tag, List<Slot> children)
    {
        foreach (var child in EnumerateAllChildren())
        {
            if (child.Tag.Value == tag)
                children.Add(child);
            child.GetChildrenWithTag(tag, children);
        }
    }

    public Slot FindChildOrAdd(string name, bool recursive = false)
    {
        var found = FindChild(name, recursive);
        return found ?? AddSlot(name);
    }

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
                    return null!;
                child = current.AddSlot(part);
            }
            current = child;
        }

        return current;
    }

    public void ForeachChild(Action<Slot> action)
    {
        foreach (var child in EnumerateAllChildren())
            action(child);
    }

    public void ForeachChildRecursive(Action<Slot> action)
    {
        foreach (var child in EnumerateAllChildren())
        {
            action(child);
            child.ForeachChildRecursive(action);
        }
    }

    // Moves a child to index among the others, clamped to the first or last place. Made by writing order
    // offsets, so it reaches every machine.
    public void MoveChildToIndex(Slot child, int index)
    {
        var writes = new List<(Slot Slot, long Offset)>();
        if (!PlanChildMove(child, index, writes))
            return;
        foreach (var (slot, offset) in writes)
            slot.OrderOffset.Value = offset;
    }

    // The offset writes that would put child at index. The child's own offset alone when its new neighbours
    // leave room; otherwise the run from it out to one end of the list is renumbered too, whichever end
    // takes fewer writes canWrite allows. False when child is not in the shared list here or no plan gets
    // past canWrite; true with nothing to write when it is already there.
    public bool PlanChildMove(Slot child, int index, List<(Slot Slot, long Offset)> writes, Func<Slot, bool>? canWrite = null)
    {
        writes.Clear();
        int at = _children.IndexOf(child);
        if (at < 0)
            return false;
        index = System.Math.Clamp(index, 0, _children.Count - 1);
        if (index == at)
            return true;

        var order = new List<Slot>(_children);
        order.RemoveAt(at);
        order.Insert(index, child);
        var before = index > 0 ? order[index - 1] : null;
        var after = index < order.Count - 1 ? order[index + 1] : null;

        if (TryOffsetBetween(before, after, child, out long between))
        {
            if (canWrite != null && !canWrite(child))
                return false;
            writes.Add((child, between));
            return true;
        }

        // Up from the slot before it to the end, or down from the slot after it to the start. With no
        // neighbour on that side the run is the whole list, numbered from 0.
        var up = new List<(Slot Slot, long Offset)>();
        bool upFits = before == null
            ? PlanRun(order, 0, order.Count - 1, 0, up, canWrite)
            : before.OrderOffset.Value <= long.MaxValue - OrderStep
                && PlanRun(order, index, order.Count - 1, before.OrderOffset.Value + OrderStep, up, canWrite);
        var down = new List<(Slot Slot, long Offset)>();
        bool downFits = after == null
            ? PlanRun(order, 0, order.Count - 1, 0, down, canWrite)
            : after.OrderOffset.Value >= long.MinValue + OrderStep * (index + 1)
                && PlanRun(order, 0, index, after.OrderOffset.Value - OrderStep * (index + 1), down, canWrite);

        if (!upFits && !downFits)
            return false;
        writes.AddRange(upFits && (!downFits || up.Count <= down.Count) ? up : down);
        return true;
    }

    // An offset strictly between the two neighbours, or equal to one of them where the RefID still puts
    // the child on the right side of it.
    private static bool TryOffsetBetween(Slot? before, Slot? after, Slot child, out long offset)
    {
        offset = 0;
        if (before == null || after == null)
        {
            if (after != null && after.OrderOffset.Value >= long.MinValue + OrderStep)
            {
                offset = after.OrderOffset.Value - OrderStep;
                return true;
            }
            if (before != null && before.OrderOffset.Value <= long.MaxValue - OrderStep)
            {
                offset = before.OrderOffset.Value + OrderStep;
                return true;
            }
            return false;
        }

        long low = before.OrderOffset.Value;
        long high = after.OrderOffset.Value;
        ulong gap = unchecked((ulong)high - (ulong)low);
        if (gap >= 2)
        {
            offset = unchecked(low + (long)(gap / 2));
            return true;
        }
        ulong id = OrderId(child);
        if (OrderId(before) < id && (gap == 1 || id < OrderId(after)))
        {
            offset = low;
            return true;
        }
        if (gap == 1 && id < OrderId(after))
        {
            offset = high;
            return true;
        }
        return false;
    }

    // Offsets rising by OrderStep across order[from..to] from first, leaving out the ones already right.
    // False when they would run past a long or canWrite refuses one.
    private static bool PlanRun(List<Slot> order, int from, int to, long first, List<(Slot Slot, long Offset)> writes,
        Func<Slot, bool>? canWrite)
    {
        writes.Clear();
        long offset = first;
        for (int i = from; i <= to; i++)
        {
            if (i > from)
            {
                if (offset > long.MaxValue - OrderStep)
                    return false;
                offset += OrderStep;
            }
            var slot = order[i];
            if (slot.OrderOffset.Value == offset)
                continue;
            if (canWrite != null && !canWrite(slot))
                return false;
            writes.Add((slot, offset));
        }
        return true;
    }

    public void InsertAtIndex(int index)
    {
        _parent?.MoveChildToIndex(this, index);
    }

    public static void SwapChildren(Slot a, Slot b)
    {
        if (a == null || b == null || ReferenceEquals(a, b))
            return;
        var parent = a._parent;
        if (parent == null || !ReferenceEquals(parent, b._parent))
            return;

        int indexA = a.SiblingIndex;
        int indexB = b.SiblingIndex;
        // Move the lower-indexed slot's target first; each MoveChildToIndex re-clamps
        // against the current list, so doing them in this order lands both correctly.
        if (indexA < indexB)
        {
            parent.MoveChildToIndex(b, indexA);
            parent.MoveChildToIndex(a, indexB);
        }
        else
        {
            parent.MoveChildToIndex(a, indexB);
            parent.MoveChildToIndex(b, indexA);
        }
    }

    public Slot InsertSlot(int index, string name = "Slot")
    {
        var slot = AddSlot(name);
        MoveChildToIndex(slot, index);
        return slot;
    }

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

    public bool IsAncestorOf(Slot slot)
    {
        return slot?.IsDescendantOf(this) ?? false;
    }

    public Slot FindCommonAncestor(Slot other)
    {
        if (other == null) return null!;

        var ancestors = new HashSet<Slot>(GetAncestors(true));
        foreach (var ancestor in other.GetAncestors(true))
        {
            if (ancestors.Contains(ancestor))
                return ancestor;
        }
        return null!;
    }

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

    public Slot Duplicate(Slot newParent = null!, bool preserveGlobalTransform = false)
    {
        // Ahead of the parent resolution below: the root has no parent, so it would otherwise fall out of
        // the next line with no word about why. -xlinka
        if (RefuseOnRoot("duplicate", RootRefusalDuplicate))
            return null!;
        if (RefusedBySeal())
            return null!;
        newParent ??= _parent;
        if (newParent == null || World == null)
            return null!;
        if (newParent == this || newParent.IsDescendantOf(this))
            return null!;

        DemandCopyScope(newParent.World);
        // Three phases:
        //  1. Clone the slot/component tree and map every source element's RefID
        //     to its clone.
        //  2. Copy member values - leaves copy immediately; every member registers
        //     its RefID in the map and every reference is deferred (a reference
        //     can't bind before its target's clone exists).
        //  3. Transfer references against the now-complete map, so a ref that
        //     pointed inside the source tree points at its clone instead of the
        //     original. This is what makes the copy a self-contained instance -
        //     drives, color drivers and any field-targeting ref follow the clone.
        var elementMap = new Dictionary<RefID, RefID>();
        var slotPairs = new List<(Slot source, Slot clone)>();
        var componentPairs = new List<(Component source, Component target)>();
        var deferredRefs = new List<(ISyncRef cloneRef, ISyncRef sourceRef)>();

        var clone = DuplicateStructure(newParent, elementMap, slotPairs, componentPairs);
        if (clone == null)
            return null!;

        foreach (var (source, target) in slotPairs)
            CopySlotMembers(source, target, elementMap, deferredRefs);

        foreach (var (source, target) in componentPairs)
            CopyComponentData(source, target, elementMap, deferredRefs);

        foreach (var (cloneRef, sourceRef) in deferredRefs)
            TransferReference(cloneRef, sourceRef, elementMap);

        if (preserveGlobalTransform)
        {
            clone.GlobalPosition = GlobalPosition;
            clone.GlobalRotation = GlobalRotation;
            clone.GlobalScale = GlobalScale;
        }

        // Post-fixup hook: runs after all data and references are in place so
        // components can rebind runtime state.
        foreach (var (_, target) in componentPairs)
        {
            try
            {
                target.OnDuplicate();
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"OnDuplicate failed for {target.GetType().Name}: {ex.Message}");
            }
        }

        return clone;
    }

    // A copy is a copy whoever asks for it, the host and a script included: an account's seal in or over this
    // object refuses everyone but its owner (Security.ItemSeals).
    private bool RefusedBySeal()
    {
        if (!Security.ItemSeals.Refuses(this, Security.SealRule.SaveCopy, out var reason))
            return false;
        Logging.Logger.Log($"Slot: refused to duplicate '{Name.Value}': {reason}");
        return true;
    }

    private void DemandCopyScope(World destination)
    {
        foreach (var component in _components)
            ComponentExecutionPolicy.Demand(component.GetType(), destination);
        foreach (var child in EnumerateAllChildren())
            child.DemandCopyScope(destination);
    }

    private Slot? DuplicateStructure(
        Slot newParent,
        Dictionary<RefID, RefID> elementMap,
        List<(Slot source, Slot clone)> slotPairs,
        List<(Component source, Component target)> componentPairs)
    {
        // An active user's root - their body, tracking and input rig - must
        // never be duplicated.
        if (ActiveUserRoot != null && ActiveUserRoot.Slot == this)
            return null;

        var clone = newParent.AddSlot(Name.Value);
        elementMap[ReferenceID] = clone.ReferenceID;
        slotPairs.Add((this, clone));

        foreach (var component in _components)
        {
            if (component.DontDuplicate)
                continue;

            // Attach behaviors stay off for clones: the copied data IS the
            // state, and OnAttach side effects (auto-attached helpers, default
            // setup) would fight it.
            var cloneComp = clone.AttachComponent(component.GetType(), runOnAttachBehavior: false);
            elementMap[component.ReferenceID] = cloneComp.ReferenceID;
            componentPairs.Add((component, cloneComp));
        }

        foreach (var child in _children)
        {
            child.DuplicateStructure(clone, elementMap, slotPairs, componentPairs);
        }

        return clone;
    }

    // All slot-level sync members copied generically (transform, name, tag,
    // active state, and anything added later); only hierarchy plumbing is
    // excluded.
    // Found once and compiled to plain reads, so copying thousands of slots asks reflection nothing per slot.
    // The parent reference is left out.
    private static (string Name, Func<Slot, ISyncMember?> Read)[]? _copiedSlotMembers;

    private static (string, Func<Slot, ISyncMember?>)[] CopiedSlotMembers()
    {
        var members = new List<(string, Func<Slot, ISyncMember?>)>();
        var slot = System.Linq.Expressions.Expression.Parameter(typeof(Slot), "slot");
        foreach (var field in typeof(Slot).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(ISyncMember).IsAssignableFrom(field.FieldType) || field.Name == nameof(ParentSlotRef))
                continue;
            var read = System.Linq.Expressions.Expression.Lambda<Func<Slot, ISyncMember?>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Field(slot, field), typeof(ISyncMember)), slot);
            members.Add((field.Name, read.Compile()));
        }
        foreach (var prop in typeof(Slot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(ISyncMember).IsAssignableFrom(prop.PropertyType) || prop.GetIndexParameters().Length != 0 || !prop.CanRead)
                continue;
            var read = System.Linq.Expressions.Expression.Lambda<Func<Slot, ISyncMember?>>(
                System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Property(slot, prop), typeof(ISyncMember)), slot);
            members.Add((prop.Name, read.Compile()));
        }
        return members.ToArray();
    }

    private void CopySlotMembers(Slot source, Slot clone, Dictionary<RefID, RefID> elementMap,
        List<(ISyncRef cloneRef, ISyncRef sourceRef)> deferredRefs)
    {
        foreach (var (name, read) in _copiedSlotMembers ??= CopiedSlotMembers())
        {
            try
            {
                CopyMemberPair(read(source), read(clone), elementMap, deferredRefs);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Failed to copy slot member {name}: {ex.Message}");
            }
        }
    }

    // Cached per-type member layout for duplication: exactly the members the worker syncs and saves, private
    // ones included, less any marked DontCopy. Going by public fields and properties left every private
    // reference of a copy empty, so a copied studio or tool rebuilt the parts it already had beside the
    // copies. -xlinka
    private sealed class CopyPlan
    {
        public int[] SyncMembers = Array.Empty<int>();
    }

    private static readonly Dictionary<Type, CopyPlan> _copyPlans = new();

    private static CopyPlan GetCopyPlan(Type type)
    {
        if (_copyPlans.TryGetValue(type, out var cached))
            return cached;

        var info = WorkerInitializer.GetInitInfo(type);
        var sync = new List<int>(info.SyncMemberFields.Length);
        for (int i = 0; i < info.SyncMemberFields.Length; i++)
        {
            if (!info.SyncMemberDontCopy[i])
                sync.Add(i);
        }

        var plan = new CopyPlan { SyncMembers = sync.ToArray() };
        _copyPlans[type] = plan;
        return plan;
    }

    private void CopyComponentData(Component source, Component target, Dictionary<RefID, RefID> elementMap,
        List<(ISyncRef cloneRef, ISyncRef sourceRef)> deferredRefs)
    {
        var plan = GetCopyPlan(source.GetType());

        // By index from each worker's own member table, filled once at init, not by reflecting every field
        // of every copied component.
        foreach (int index in plan.SyncMembers)
        {
            try
            {
                CopyMemberPair(source.GetSyncMember(index), target.GetSyncMember(index), elementMap, deferredRefs);
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Failed to copy member {source.GetSyncMemberName(index)}: {ex.Message}");
            }
        }
    }

    private void CopyMemberPair(ISyncMember? sourceMember, ISyncMember? targetMember,
        Dictionary<RefID, RefID> elementMap, List<(ISyncRef cloneRef, ISyncRef sourceRef)> deferredRefs)
    {
        if (sourceMember != null && targetMember != null)
        {
            CopySyncMemberValue(sourceMember, targetMember, elementMap, deferredRefs);
        }
    }

    // Every member registers its RefID in the map so references aimed at it can be remapped; references
    // themselves are deferred and resolved once the whole tree exists.
    private void CopySyncMemberValue(ISyncMember source, ISyncMember target,
        Dictionary<RefID, RefID> elementMap, List<(ISyncRef cloneRef, ISyncRef sourceRef)> deferredRefs)
    {
        var sourceType = source.GetType();
        var targetType = target.GetType();

        if (sourceType != targetType) return;

        // Register this member (field, ref, or list element) so any reference in
        // the tree that targets it can be relocated to the clone's member. Without
        // this a ref aimed at a field stays bound to the original's field.
        elementMap[source.ReferenceID] = target.ReferenceID;

        // A member only the host may author is left for the host to fill on a guest's copy. Copied here it went
        // out as this guest's write, the host refused it as a host-only write, and the few on one scripted avatar
        // were enough to have the guest kicked for wearing a copy of it. -xlinka
        if (target is ConflictingSyncElement { IsHostOnly: true } hostOnly && hostOnly.World?.IsAuthority == false)
            return;

        // References (SyncRef and subclasses like AssetRef) are deferred, not bound
        // here: the target's clone may not exist yet. The transfer phase resolves
        // them against the complete map.
        if (source is ISyncRef sourceRef && target is ISyncRef targetRef)
        {
            // Delegates carry a method name (and possibly a static type) beside the
            // target; copy that now so the clone keeps the same handler once its
            // target is remapped in the transfer phase.
            if (source is ISyncDelegate sourceDelegate && target is ISyncDelegate targetDelegate)
                targetDelegate.SetMethod(sourceDelegate.MethodName, sourceDelegate.StaticType);

            deferredRefs.Add((targetRef, sourceRef));
            return;
        }

        // Lists (SyncList/SyncAssetList): grow the clone's list and copy
        // element-wise, recursing so elements register and nested refs defer.
        if (source is ISyncList sourceList && target is ISyncList targetList)
        {
            int count = sourceList.Count;
            for (int i = 0; i < count; i++)
            {
                var sourceElement = sourceList.GetElement(i);
                var targetElement = i < targetList.Count ? targetList.GetElement(i) : targetList.AddElement();
                if (sourceElement != null && targetElement != null)
                {
                    CopySyncMemberValue(sourceElement, targetElement, elementMap, deferredRefs);
                }
            }
            return;
        }

        // Flat value collections and composite entries have no Value property and no elements to walk,
        // so they copy themselves; the callback keeps nested references on the deferred path.
        if (source is ISyncMemberCopy && target is ISyncMemberCopy targetCopy)
        {
            targetCopy.CopyFromSource(source, (s, t) => CopySyncMemberValue(s, t, elementMap, deferredRefs));
            return;
        }

        // A composite entry that does not copy itself (a LOD level, a gradient stop) is its members, walked like
        // any other; passed over, every entry of a copy came out at its defaults. -xlinka
        if (source is SyncElement { MemberType: SyncMemberType.Object })
        {
            var copied = new List<ISyncMember>();
            foreach (var member in CompositeMembers(sourceType))
            {
                if (MemberValue(member, source) is not ISyncMember from || MemberValue(member, target) is not ISyncMember to
                    || copied.Contains(from))
                    continue;
                copied.Add(from);
                CopySyncMemberValue(from, to, elementMap, deferredRefs);
            }
            return;
        }

        // A plain field takes the value through its own setter, the same one the reflected Value would reach,
        // without looking the property up and invoking it for every member of every copy.
        if (source is IField sourceField && target is IField targetField)
        {
            var boxed = sourceField.BoxedValue;
            if (boxed != null)
            {
                try
                {
                    targetField.BoxedValue = boxed;
                }
                catch
                {
                }
            }
            return;
        }

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
                }
            }
        }
    }

    // The members a composite entry holds, found the way SyncMemberDiscovery finds them.
    private static readonly Dictionary<Type, MemberInfo[]> _compositeMembers = new();

    private static MemberInfo[] CompositeMembers(Type type)
    {
        lock (_compositeMembers)
        {
            if (_compositeMembers.TryGetValue(type, out var known))
                return known;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var members = new List<MemberInfo>();
            foreach (var field in type.GetFields(flags))
            {
                if (typeof(ISyncMember).IsAssignableFrom(field.FieldType))
                    members.Add(field);
            }
            foreach (var property in type.GetProperties(flags))
            {
                if (typeof(ISyncMember).IsAssignableFrom(property.PropertyType) && property.CanRead
                    && property.GetIndexParameters().Length == 0)
                    members.Add(property);
            }
            known = members.ToArray();
            _compositeMembers[type] = known;
            return known;
        }
    }

    private static object? MemberValue(MemberInfo member, object owner)
    {
        try
        {
            return member switch
            {
                FieldInfo field => field.GetValue(owner),
                PropertyInfo property => property.GetValue(owner),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    // Resolve a deferred reference now that the whole clone tree and its member
    // map exist. A ref whose target lived inside the source tree points at the
    // clone of that target; an external non-link ref stays shared with the
    // original (materials, fonts); an external link is left null - a forked link
    // would fight the original over the same target.
    private void TransferReference(ISyncRef cloneRef, ISyncRef sourceRef, Dictionary<RefID, RefID> elementMap)
    {
        var sourceTarget = sourceRef.Value;
        if (sourceTarget == RefID.Null)
            return;

        if (elementMap.TryGetValue(sourceTarget, out var clonedTarget))
            cloneRef.Value = clonedTarget;
        else if (sourceRef is not ILinkRef)
            cloneRef.Value = sourceTarget;
    }

    public Slot DeepCopy(Slot newParent = null!)
    {
        return Duplicate(newParent, false);
    }

    // Several slots copied in one pass, each under the parent given beside it: a reference from one of them into
    // another follows the copy, the way one inside a single slot's tree does, and anything else is kept or left
    // null exactly as Duplicate keeps or leaves it. The copies come back in the order given, null where one could
    // not be made; a slot under another one given comes along inside that one's copy. -xlinka
    public static List<Slot?> DuplicateTogether(IReadOnlyList<Slot> sources, IReadOnlyList<Slot> parents, bool preserveGlobalTransform = false)
    {
        // Validate every source before the first clone; a rejected personal panel must leave no
        // partial copy of its local labels or contents in a shared world.
        for (int i = 0; i < sources.Count && i < parents.Count; i++)
            if (sources[i] is { IsDestroyed: false } source && parents[i] is { IsDestroyed: false } parent)
                source.DemandCopyScope(parent.World);
        var copies = new List<Slot?>(sources.Count);
        var elementMap = new Dictionary<RefID, RefID>();
        var slotPairs = new List<(Slot source, Slot clone)>();
        var componentPairs = new List<(Component source, Component target)>();
        var deferredRefs = new List<(ISyncRef cloneRef, ISyncRef sourceRef)>();
        var roots = new List<(Slot source, Slot clone)>();

        for (int i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var parent = i < parents.Count ? parents[i] : null;
            Slot? clone = null;
            if (source != null && !source.IsDestroyed && source.World != null && parent != null && !parent.IsDestroyed
                && !ReferenceEquals(parent, source) && !parent.IsDescendantOf(source) && !InsideAnother(source, sources)
                && !source.RefuseOnRoot("duplicate", RootRefusalDuplicate) && !source.RefusedBySeal())
            {
                clone = source.DuplicateStructure(parent, elementMap, slotPairs, componentPairs);
                if (clone != null)
                    roots.Add((source, clone));
            }
            copies.Add(clone);
        }
        if (roots.Count == 0)
            return copies;

        var first = roots[0].source;
        foreach (var (source, target) in slotPairs)
            first.CopySlotMembers(source, target, elementMap, deferredRefs);

        foreach (var (source, target) in componentPairs)
            first.CopyComponentData(source, target, elementMap, deferredRefs);

        foreach (var (cloneRef, sourceRef) in deferredRefs)
            first.TransferReference(cloneRef, sourceRef, elementMap);

        if (preserveGlobalTransform)
        {
            foreach (var (source, clone) in roots)
            {
                clone.GlobalPosition = source.GlobalPosition;
                clone.GlobalRotation = source.GlobalRotation;
                clone.GlobalScale = source.GlobalScale;
            }
        }

        foreach (var (_, target) in componentPairs)
        {
            try
            {
                target.OnDuplicate();
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"OnDuplicate failed for {target.GetType().Name}: {ex.Message}");
            }
        }

        return copies;
    }

    private static bool InsideAnother(Slot slot, IReadOnlyList<Slot> sources)
    {
        foreach (var other in sources)
        {
            if (other != null && !ReferenceEquals(other, slot) && slot.IsDescendantOf(other))
                return true;
        }
        return false;
    }

    #endregion

    #region Path & Hierarchy

    public string GetPath()
    {
        if (_parent == null)
            return Name.Value;
        return _parent.GetPath() + "/" + Name.Value;
    }

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

        var ancestor = FindCommonAncestor(relativeTo);
        if (ancestor == null)
            return GetPath();

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

    public override string ParentHierarchyToString()
    {
        return GetPath();
    }

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

    // Applying another peer's destroy runs as the system, and whatever that sets off on this peer meanwhile
    // (a prepare-destroy handler, anything else run inside it) destroying some other slot or component is
    // judged as the peer that destroy speaks for: this peer on a client, where anything it destroys goes out
    // as its own, and the sender on the host, where the bypass would otherwise hand a guest's destroy the
    // host's reach. Under the bypass a client's handler took down something it did not own and sent it; the
    // host refused it, counted it toward a kick and sent the slot back as an orphan. -xlinka
    [ThreadStatic] private static int s_replicatedDestroyDepth;
    [ThreadStatic] private static User? s_replicatedDestroyActor;
    private static bool s_loggedNestedRefusal;

    internal static bool InsideReplicatedDestroy => s_replicatedDestroyDepth > 0;

    // Whether the actor this machine answers for right now may take a registry entry out: as things stand,
    // or while a replicated destroy is being applied, as the peer it speaks for with the bypass set aside.
    internal static bool MayTakeOut<T>(Networking.Sync.ReplicatedDictionary<RefID, T> collection, in RefID id)
        where T : class, IWorldElement
        => s_replicatedDestroyDepth == 0
            ? collection.MayRemoveQuietly(id)
            : s_replicatedDestroyActor is { } actor && collection.MayRemoveQuietly(id, actor, unscoped: true);

    // Whether this destroy is one a replicated destroy set off and the peer it speaks for may not make.
    // Logged once, at debug level: the slot is left as it is, and its owner's own destroy still reaches it.
    private bool RefusedInsideReplicatedDestroy(World world)
    {
        if (s_replicatedDestroyDepth == 0 || IsLocalElement || world.SlotRegistry is not { } registry
            || !registry.ContainsKey(ReferenceID) || MayTakeOut(registry, ReferenceID))
            return false;
        if (!s_loggedNestedRefusal)
        {
            s_loggedNestedRefusal = true;
            Logging.Logger.Debug($"Slot.Destroy: '{Name.Value}' left alone; another peer's destroy set it off here and it is not {s_replicatedDestroyActor?.UserName.Value ?? "anyone"}'s to remove");
        }
        return true;
    }

    // What Destroy asks of the slot itself before it takes anything apart, asked without touching anything
    // or saying a word: false for the world root, a protected slot, or a removal this actor may not make. An
    // undoable delete asks it before parking, since parking is only a move and would go through where the
    // destroy it stands for will not.
    public bool MayDestroy()
    {
        if (IsDestroyed)
            return false;
        var world = World;
        if (world == null || world.IsDisposed || world.IsDestroyed)
            return true;
        if (IsProtected)
            return false;
        if (IsLocalElement)
            return true;
        return world.SlotRegistry is not { IsLoading: false, IsInInitPhase: false } registry
            || !registry.ContainsKey(ReferenceID) || registry.MayRemoveQuietly(ReferenceID);
    }

    // The same question asked for someone else, as they stand with no scope open: whether that user could
    // take this down by hand.
    internal bool MayBeDestroyedBy(User actor)
    {
        if (IsDestroyed || IsProtected || World is not { } world || world.IsDisposed || world.IsDestroyed)
            return false;
        return IsLocalElement || world.SlotRegistry is not { } registry || !registry.ContainsKey(ReferenceID)
            || registry.MayRemoveQuietly(ReferenceID, actor, unscoped: true);
    }

    // Someone other than this machine's user holding it in place: a lever or hinge part, which a grab leaves
    // where it hangs. A carried object hangs under its holder's hand, outside whatever it was taken from.
    private static bool HeldByAnother(Slot slot)
    {
        var holder = ((IPermissionGrabSurface)slot).CurrentHolder;
        return holder != null && !ReferenceEquals(holder, slot.World?.LocalUser);
    }

    public void Destroy()
    {
        if (IsDestroyed) return;
        // Teardown is exempt: a world being disposed takes its root down with it, and by then the world
        // has already said it is gone. Everything else asking is asking from inside a live world. -xlinka
        bool teardown = World == null || World.IsDisposed || World.IsDestroyed;
        if (IsProtected && !teardown)
        {
            if (RefuseOnRoot("destroy", RootRefusalDestroy))
                return;
            Logging.Logger.Warn($"Slot.Destroy: refusing to destroy protected slot '{Name.Value}' (RefID {ReferenceID})");
            return;
        }

        if (!teardown && RefusedInsideReplicatedDestroy(World!))
            return;

        // The slot's own removal is asked before anything is taken apart. Refused at the end, as it was, it
        // left the slot marked destroyed with its components stripped on this machine and still registered
        // everywhere; on the authority that was the world every later joiner got, a frozen world's props
        // among it. -xlinka
        if (!teardown && !IsLocalElement && World?.SlotRegistry is { IsLoading: false, IsInInitPhase: false } own
            && own.ContainsKey(ReferenceID) && !own.MayRemove(ReferenceID))
            throw new UnauthorizedAccessException("datamodel mutation denied: destroying this slot");

        IsDestroyed = true;
        OnPrepareDestroy?.Invoke(this);

        // What the actor may not destroy is not destroyed: a child someone else owns or is holding moves to
        // the world root instead, and a component the actor may not remove leaves this peer only, the authority's
        // copy of the destroy taking it away everywhere else. This used to throw part way through, on a
        // slot already marked destroyed and still registered. -xlinka
        var registry = World?.SlotRegistry;
        var survivorParent = SurvivorParent();
        foreach (var child in _children.ToArray())
        {
            // Hung somewhere else while this slot was going: a seat or a carrier letting its rider down. The
            // lists of a slot being destroyed are only emptied at the end, so the entry is still here.
            if (!ReferenceEquals(child._parent, this))
                continue;
            // A person riding or sitting on what goes keeps the body: it is let down onto the world. A user
            // root goes only when it is destroyed itself.
            if (!teardown && ((child.ActiveUserRoot is { IsDestroyed: false } body && ReferenceEquals(body.Slot, child))
                || HeldByAnother(child)))
            {
                MoveSurvivorOut(child, survivorParent, fromReplication: s_replicatedDestroyDepth > 0);
                continue;
            }
            if (teardown || child.IsLocalElement || registry == null || MayTakeOut(registry, child.ReferenceID))
                child.Destroy();
            else
                MoveSurvivorOut(child, survivorParent, fromReplication: s_replicatedDestroyDepth > 0);
        }
        foreach (var child in _localChildren.ToArray())
            child.Destroy();

        foreach (var component in _components.ToArray())
        {
            if (teardown || IsLocalElement || MayRemoveComponentQuietly(component))
            {
                RemoveComponent(component);
            }
            else
            {
                using (World?.DataModelPermissions?.EnterSystemBypass())
                    RemoveComponentWithoutSync(component);
            }
        }

		_children.Clear();
		_localChildren.Clear();

        _parent?.DetachChildInternal(this);

        World?.ReferenceController?.UnregisterObject(this);
        World?.UnregisterSlot(this);

        Hook?.Dispose();
        DisposeSlotSyncMembers();
    }

    // A peer destroyed this slot, and the registry removal saying so has just been applied here. The
    // destroying peer sent a removal for everything under it that it was allowed to destroy, children
    // first, so whatever is still alive under this slot is what it could not: someone's held object,
    // another user's things. Those survive at the world root. Components go with the slot. Nothing here
    // sends a removal back out: every peer applies the same records, and an echo from a guest would read
    // as that guest destroying other people's things. Runs as the system, on state the authority has
    // already judged. -xlinka
    internal void DestroyFromReplication(User? remover = null)
    {
        var world = World;
        if (IsDestroyed || IsProtected || world == null || world.IsDisposed)
            return;

        var outerActor = s_replicatedDestroyActor;
        s_replicatedDestroyActor = world.IsAuthority ? remover : world.LocalUser;
        s_replicatedDestroyDepth++;
        try
        {
            ApplyReplicatedDestroy(world);
        }
        finally
        {
            s_replicatedDestroyDepth--;
            s_replicatedDestroyActor = outerActor;
        }
    }

    private void ApplyReplicatedDestroy(World world)
    {
        using var system = world.DataModelPermissions?.EnterSystemBypass();
        IsDestroyed = true;
        OnPrepareDestroy?.Invoke(this);

        var survivorParent = SurvivorParent();
        foreach (var child in _children.ToArray())
        {
            if (!child.IsDestroyed && ReferenceEquals(child._parent, this))
                MoveSurvivorOut(child, survivorParent, fromReplication: true);
        }
        foreach (var child in _localChildren.ToArray())
            child.Destroy();

        foreach (var component in _components.ToArray())
            RemoveComponentWithoutSync(component);

        _children.Clear();
        _localChildren.Clear();

        _parent?.DetachChildInternal(this);

        world.ReferenceController?.UnregisterObject(this);
        world.UnregisterSlot(this);

        Hook?.Dispose();
        DisposeSlotSyncMembers();
    }

    private void DisposeSlotSyncMembers()
    {
        // Removing a slot from the registry does not remove its fields from full-state snapshots.
        // Package imports prune many temporary slots; their surviving fields otherwise refer to
        // slots a joining peer will never receive. Release them on both local and replicated deletion.
        for (int i = 0; i < SyncMemberCount; i++)
            GetSyncMember(i)?.Dispose();
    }

    // The world root, where they stay in place. Never up a level: whatever the destroyer could not
    // destroy is by definition not theirs, and the level above can be inside the destroyer's own
    // hierarchy, where a world object would read as structurally theirs to edit. -xlinka
    private Slot? SurvivorParent() => World?.RootSlot;

    // The authority writes the move, as the system, so every peer ends up agreeing. Anyone else moves it
    // with a real write when it may, which is how a holder lets go of what it holds, and otherwise only
    // here, until the authority's write lands. A receiver never writes: the authority already has.
    private static void MoveSurvivorOut(Slot child, Slot? newParent, bool fromReplication)
    {
        if (newParent == null || child.IsDestroyed)
            return;

        var world = child.World;
        // A person's body is moved by the person's own machine, where it stands; it streams its pose in the space it
        // hangs from, so a move made anywhere else would be undone by the next sample. Elsewhere it hangs from
        // the new parent on this machine only until the owner's move arrives.
        if (child.ActiveUserRoot is { IsDestroyed: false } body && ReferenceEquals(body.Slot, child) && body.ActiveUser is { } person)
        {
            if (person.IsLocal)
            {
                using (world?.DataModelPermissions?.EnterSystemBypass())
                    child.SetParent(newParent, preserveGlobalTransform: true);
            }
            else
            {
                child.ReattachLocally(newParent);
            }
            return;
        }
        if (world?.IsAuthority == true)
        {
            using (world.DataModelPermissions?.EnterSystemBypass())
                child.SetParent(newParent, preserveGlobalTransform: true);
        }
        else if (!fromReplication && child.ParentSlotRef.IsWriteAllowedQuietly())
        {
            child.SetParent(newParent, preserveGlobalTransform: true);
        }
        else
        {
            child.ReattachLocally(newParent);
        }
    }

    // An undoable delete parks the slot out of sight instead of destroying it, and what the destroy would
    // leave standing went into the pen with the rest: a person riding it, something another person holds
    // where it hangs, another user's thing the actor may not take down. These are what a park has to let
    // out first. False when one of them can only be let out by some other machine (another person's body,
    // a hold, a slot this one may not write): a park is a plain move nobody else answers, where a destroy is
    // answered by every peer, so the caller destroys for real then. -xlinka
    internal bool CollectParkSurvivors(List<Slot> into)
    {
        into.Clear();
        CollectSurvivors(this, into);
        bool authority = World?.IsAuthority == true;
        foreach (var survivor in into)
        {
            bool movable = survivor.ActiveUserRoot is { IsDestroyed: false } body && ReferenceEquals(body.Slot, survivor)
                ? body.ActiveUser is { IsLocal: true }
                : authority || (!HeldByAnother(survivor) && survivor.ParentSlotRef.IsWriteAllowedQuietly());
            if (!movable)
            {
                into.Clear();
                return false;
            }
        }
        return true;
    }

    private static void CollectSurvivors(Slot slot, List<Slot> into)
    {
        var registry = slot.World?.SlotRegistry;
        foreach (var child in slot._children)
        {
            if (child.IsDestroyed || child.IsLocalElement || !ReferenceEquals(child._parent, slot))
                continue;
            if ((child.ActiveUserRoot is { IsDestroyed: false } body && ReferenceEquals(body.Slot, child))
                || HeldByAnother(child) || (registry != null && !MayTakeOut(registry, child.ReferenceID)))
                into.Add(child);
            else
                CollectSurvivors(child, into);
        }
    }

    // Out to the world root, moved the way a destroy moves what it leaves standing.
    internal static void LetOut(Slot survivor) => MoveSurvivorOut(survivor, survivor.World?.RootSlot, fromReplication: false);

    // And back again on undo, written the same way: as the system on the authority, else only where this
    // machine may write it.
    internal static void HangBack(Slot survivor, Slot parent, float3 position, floatQ rotation, float3 scale)
    {
        var world = survivor.World;
        bool authority = world?.IsAuthority == true;
        if (!authority && !survivor.ParentSlotRef.IsWriteAllowedQuietly())
            return;
        using (authority ? world!.DataModelPermissions?.EnterSystemBypass() : default)
        {
            survivor.SetParent(parent, preserveGlobalTransform: false);
            survivor.LocalPosition.Value = position;
            survivor.LocalRotation.Value = rotation;
            survivor.LocalScale.Value = scale;
        }
    }

    // Surviving providers keep their slots, which get reparented under relocateAssets (created under the world
    // root as "<name> - Assets" when null); everything else is destroyed.
    public void DestroyPreservingAssets(Slot? relocateAssets = null, ISlotStripRecorder? recorder = null)
    {
        if (RefuseOnRoot("destroy keeping assets", RootRefusalStrip))
            return;
        if (IsDestroyed || IsProtected || World == null)
            return;
        if (relocateAssets != null && (relocateAssets == this || relocateAssets.IsDescendantOf(this)))
            return;
        // Asked before the strip, as Destroy asks it: refused at the end, the strip had already taken the
        // components off on this machine.
        if (!MayDestroy())
            throw new UnauthorizedAccessException("datamodel mutation denied: destroying this slot");

        var hierarchy = new HashSet<Slot>();
        GenerateHierarchy(hierarchy);

        Slot? target = relocateAssets;
        Slot GetRelocationTarget()
        {
            if (target != null)
                return target;
            target = World.RootSlot.AddSlot(Name.Value + " - Assets");
            recorder?.HolderCreated(target);
            return target;
        }

        var externallyReferenced = new Dictionary<IAssetProvider, bool>();
        StripNonAssetComponents(hierarchy, externallyReferenced, recorder);
        RelocateOrDestroyEmpty(GetRelocationTarget, recorder);
    }

    // The question DestroyPreservingAssets acts on, asked without touching anything: does something outside
    // this subtree still wear an asset that lives in it.
    public bool HasAssetsReferencedOutside()
    {
        var hierarchy = new HashSet<Slot>();
        GenerateHierarchy(hierarchy);
        var memo = new Dictionary<IAssetProvider, bool>();
        foreach (var slot in hierarchy)
        {
            foreach (var component in slot.Components)
            {
                if (component is IAssetProvider provider && !component.IsDestroyed
                    && IsExternallyReferenced(provider, hierarchy, memo))
                    return true;
            }
        }
        return false;
    }

    // Bottom-up: drop every component that is not an externally-referenced asset provider.
    // Support components marked PreserveWithAssets ride along only next to a surviving provider.
    private void StripNonAssetComponents(HashSet<Slot> hierarchy, Dictionary<IAssetProvider, bool> memo,
        ISlotStripRecorder? recorder)
    {
        for (int i = _children.Count - 1; i >= 0; i--)
            _children[i].StripNonAssetComponents(hierarchy, memo, recorder);
        for (int i = _localChildren.Count - 1; i >= 0; i--)
            _localChildren[i].StripNonAssetComponents(hierarchy, memo, recorder);

        for (int i = _components.Count - 1; i >= 0; i--)
        {
            var component = _components[i];
            if (component.IsDestroyed)
                continue;
            if (component.PreserveWithAssets)
                continue;
            if (component is IAssetProvider provider && IsExternallyReferenced(provider, hierarchy, memo))
                continue;
            recorder?.ComponentStripped(component);
            component.Destroy();
        }

        bool hasProvider = false;
        foreach (var component in _components)
        {
            if (component is IAssetProvider)
            {
                hasProvider = true;
                break;
            }
        }
        if (!hasProvider)
        {
            for (int i = _components.Count - 1; i >= 0; i--)
            {
                if (_components[i].PreserveWithAssets)
                {
                    recorder?.ComponentStripped(_components[i]);
                    _components[i].Destroy();
                }
            }
        }
    }

    // Bottom-up: slots stripped bare are destroyed; slots that kept components move (flattened)
    // under the relocation target. A recorder can claim an emptied slot instead of losing it.
    private void RelocateOrDestroyEmpty(Func<Slot> getRelocationTarget, ISlotStripRecorder? recorder)
    {
        for (int i = _children.Count - 1; i >= 0; i--)
            _children[i].RelocateOrDestroyEmpty(getRelocationTarget, recorder);
        for (int i = _localChildren.Count - 1; i >= 0; i--)
            _localChildren[i].RelocateOrDestroyEmpty(getRelocationTarget, recorder);

        if (_components.Count == 0)
        {
            if (recorder?.SlotEmptied(this) != true)
                Destroy();
            return;
        }

        var holder = getRelocationTarget();
        recorder?.SlotRelocating(this, holder);
        SetParent(holder);
    }

    // A provider survives when a reference to it lives outside the doomed subtree, directly or
    // through a chain of providers (a mesh referenced by a preserved material is preserved too).
    // The memo doubles as the cycle guard: a provider currently being evaluated reads as
    // not-external, so reference loops cannot recurse forever. - xlinka
    private static bool IsExternallyReferenced(IAssetProvider provider, HashSet<Slot> hierarchy,
        Dictionary<IAssetProvider, bool> memo)
    {
        if (memo.TryGetValue(provider, out bool cached))
            return cached;
        memo[provider] = false;

        bool external = false;
        foreach (var reference in provider.References)
        {
            var element = reference as IWorldElement;
            var owner = FindOwningSlot(element);
            if (owner == null || owner.IsDestroyed)
                continue;
            if (!hierarchy.Contains(owner))
            {
                external = true;
                break;
            }
            var owningProvider = FindOwningProvider(element);
            if (owningProvider != null && !ReferenceEquals(owningProvider, provider)
                && IsExternallyReferenced(owningProvider, hierarchy, memo))
            {
                external = true;
                break;
            }
        }
        memo[provider] = external;
        return external;
    }

    private static Slot? FindOwningSlot(IWorldElement? element)
    {
        while (element != null)
        {
            if (element is Slot slot)
                return slot;
            if (element is Component component)
                return component.Slot;
            element = (element as SyncElement)?.Parent;
        }
        return null;
    }

    private static IAssetProvider? FindOwningProvider(IWorldElement? element)
    {
        while (element != null)
        {
            if (element is Component component)
                return component as IAssetProvider;
            element = (element as SyncElement)?.Parent;
        }
        return null;
    }

    public void RemoveFromHierarchy()
    {
        if (RefuseOnRoot("removal from the hierarchy", RootRefusalRemove))
            return;
        if (IsProtected)
        {
            Logging.Logger.Warn($"Slot.RemoveFromHierarchy: refusing on protected slot '{Name.Value}' (RefID {ReferenceID})");
            return;
        }

        _isRemoved = true;
        _parent?.DetachChildInternal(this);
        _parent = null!;
        ActiveStateMoved();
        System.Threading.Interlocked.Increment(ref s_hierarchyEpoch);
    }

    // Protected children are skipped; their Destroy() no-ops with a warning.
    public void DestroyChildren()
    {
        foreach (var child in _children.ToArray())
            child.Destroy();
        foreach (var child in _localChildren.ToArray())
            child.Destroy();
    }

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

    public void DestroyDelayed(float seconds)
    {
        int updates = (int)(seconds * 60); // Approximate 60 fps
        RunInUpdates(updates, Destroy);
    }

    #endregion

    #region Update

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
        QueueHookUpdate();
        Changed?.Invoke(this);
    }

    private void QueueHookUpdate()
    {
        if (Hook == null || World == null || IsDestroyed)
        {
            return;
        }

        World.UpdateManager?.RegisterHookUpdate(this);
    }

    #endregion

    #region Network Serialization

    public void Encode(BinaryWriter writer)
    {
        writer.Write((ulong)ReferenceID);
        writer.Write((ulong)(_parent?.ReferenceID ?? RefID.Null));

        EncodeSyncField(writer, Name);
        EncodeSyncField(writer, ActiveSelf);
        EncodeSyncField(writer, LocalPosition);
        EncodeSyncField(writer, LocalRotation);
        EncodeSyncField(writer, LocalScale);
        EncodeSyncField(writer, Tag);
        EncodeSyncField(writer, Persistent);
        EncodeSyncField(writer, OrderOffset);

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

    public void Decode(BinaryReader reader, Dictionary<RefID, object> refLookup)
    {
        var refId = new RefID(reader.ReadUInt64());
        var parentRefId = new RefID(reader.ReadUInt64());

        DecodeSyncField(reader, Name);
        DecodeSyncField(reader, ActiveSelf);
        DecodeSyncField(reader, LocalPosition);
        DecodeSyncField(reader, LocalRotation);
        DecodeSyncField(reader, LocalScale);
        DecodeSyncField(reader, Tag);
        DecodeSyncField(reader, Persistent);
        DecodeSyncField(reader, OrderOffset);

        int compCount = reader.ReadInt32();
        for (int i = 0; i < compCount; i++)
        {
            var typeName = reader.ReadString();
            var compRefId = new RefID(reader.ReadUInt64());

            var type = Type.GetType(typeName);
            if (type != null)
            {
                // Decoded data is the component's state; attach behaviors would
                // overwrite it with defaults and auto-attach duplicate helpers.
                var comp = AttachComponent(type, runOnAttachBehavior: false);
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

    public void SaveToBinary(BinaryWriter writer)
    {
        writer.Write("SLOT"); // Magic
        writer.Write(1); // Version

        writer.Write(Name.Value ?? "");
        writer.Write(ActiveSelf.Value);
        writer.Write(Tag.Value ?? "");
        writer.Write(Persistent.Value);
        writer.Write(OrderOffset.Value);

        WriteFloat3(writer, LocalPosition.Value);
        WriteFloatQ(writer, LocalRotation.Value);
        WriteFloat3(writer, LocalScale.Value);

        writer.Write(_components.Count);
        foreach (var comp in _components)
        {
            writer.Write(comp.GetType().AssemblyQualifiedName ?? comp.GetType().FullName ?? "");
            SaveComponentToBinary(writer, comp);
        }

        writer.Write(_children.Count);
        foreach (var child in _children)
            child.SaveToBinary(writer);
    }

    public void LoadFromBinary(BinaryReader reader)
    {
        var magic = reader.ReadString();
        if (magic != "SLOT")
            throw new InvalidDataException("Invalid slot data format");

        var version = reader.ReadInt32();
        if (version > 1)
            throw new InvalidDataException($"Unsupported slot version: {version}");

        Name.Value = reader.ReadString();
        ActiveSelf.Value = reader.ReadBoolean();
        Tag.Value = reader.ReadString();
        Persistent.Value = reader.ReadBoolean();
        OrderOffset.Value = reader.ReadInt64();

        LocalPosition.Value = ReadFloat3(reader);
        LocalRotation.Value = ReadFloatQ(reader);
        LocalScale.Value = ReadFloat3(reader);

        int compCount = reader.ReadInt32();
        for (int i = 0; i < compCount; i++)
        {
            var typeName = reader.ReadString();
            var type = Type.GetType(typeName);
            if (type != null)
            {
                // Same as decode: loaded data is the state, attach behaviors off.
                var comp = AttachComponent(type, runOnAttachBehavior: false);
                LoadComponentFromBinary(reader, comp);
            }
            else
            {
                SkipComponentData(reader);
            }
        }

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
                WriteValue(writer, value!);
            }
            else
            {
                WriteValue(writer, null!);
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
                try
                {
                    valueProp.SetValue(member, value);
                }
                catch
                {
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
            writer.Write((byte)255);
            writer.Write(value.ToString() ?? "");
        }
    }

    private object ReadValue(BinaryReader reader)
    {
        var typeCode = reader.ReadByte();

        return typeCode switch
        {
            0 => null!,
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
            _ => null!
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
