// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Core.Components.Gizmos;

// Base of the per-component gizmos: a wireframe of whatever extent a component actually has (a
// light's reach, a collider's shape, a camera's frustum) plus optional pads that drag the fields
// that extent is made of.
//
// Lives on its own slot under the world's shared Gizmos container, non-persistent, owned by the user
// who put it up, and indexed in WorldGizmos by target component so a second toggle
// finds it in O(1). Unlike the slot gizmo it never spawns on selection: a component gizmo is
// explicit, because a slot with ten components would otherwise put ten overlapping wireframes on
// screen the moment you clicked it.
//
// No shape work runs per frame. The shape is emitted once and then rides the scene graph: the gizmo
// slot mirrors the target's global pose, and a child slot carries the target's global SCALE so
// component-local units (a collider Size, a light Range) draw at the right size inside a scaled
// hierarchy without any of it being re-derived. A re-emit happens only when one of the fields the
// shape reads changes, and those are subscribed individually and coalesced to one rebuild per frame.
// A target that moves, spins or is scaled costs three guarded transform writes and no geometry work
// at all. -xlinka
public abstract class ComponentGizmo : ImplementableComponent, IComponentGizmo, IGizmoDragHost
{
    public readonly SyncRef<Component> Target;

    // synced so the authority can clear it when that user leaves; never persisted - editor chrome, and
    // the user doesn't outlive the session either
    [NonPersistent]
    public readonly SyncRef<User> Owner;

    // there is no park pool here - each of these is a different type and cannot be re-pointed at
    // another kind of component - so the toggle destroys; this exists for hiding one without losing it
    public readonly Sync<bool> Active;

    private readonly GizmoWireBuilder _wire = new();
    private readonly List<IChangeable> _watched = new();
    private readonly List<ExtentHandle> _handles = new();

    private Component? _watchedComponent;
    private Slot? _watchedSlot;
    private Slot? _followSlot;
    private Slot? _shapeRoot;
    private bool _shapeDirty;
    private bool _rebuildQueued;
    private bool _handlesBuilt;
    private int _wireVersion;

    // Only the peer that ran Setup builds this gizmo's slots. Everywhere else the rig arrives over the
    // wire, and a second peer adding its own "Shape" slot would just leave a duplicate sitting next to
    // the real one for the rest of the session, so remote peers resolve by name and wait. -xlinka
    private bool _buildsChrome;
    private int _draggingHandles;

    // Guarded follow state, so a target that moves every frame writes nothing when the values land on
    // the same numbers (a parented object riding a still hierarchy).
    private float3 _followPosition = new(float.NaN, 0f, 0f);
    private floatQ _followRotation = new(float.NaN, 0f, 0f, 1f);
    private float3 _followScale = new(float.NaN, 1f, 1f);

    protected ComponentGizmo()
    {
        Target = new SyncRef<Component>(this);
        Owner = new SyncRef<User>(this);
        Active = new Sync<bool>(this, true);
    }

    // PUBLIC SURFACE

    public Component? TargetComponent => Target?.Target is { IsDestroyed: false } component ? component : null;

    bool IComponentGizmo.IsActive
    {
        get => Active?.Value ?? false;
        set
        {
            if (Active != null)
                Active.Value = value;
        }
    }

    // read in pairs, in ShapeRoot space
    public IReadOnlyList<float3> WireVertices => _wire.Vertices;

    public IReadOnlyList<color> WireColors => _wire.Colors;

    // bumped only when the wireframe actually changed, so the hook can skip a redraw
    public int WireVersion => _wireVersion;

    // carries the target's global scale; shapes are emitted in the component's own local units and the
    // transform does the rest
    public Slot? ShapeRoot => _shapeRoot is { IsRemoved: false, IsDestroyed: false } ? _shapeRoot : null;

    public bool IsInteracting => _draggingHandles > 0;

    public bool IsVisible => Active.Value && Enabled.Value && TargetComponent != null;

    // SETUP AND TEARDOWN

    public void Setup(Component target) => Setup(target, World?.LocalUser);

    public void Setup(Component target, User? owner)
    {
        if (target == null || target.IsDestroyed)
            return;

        _buildsChrome = true;
        Owner.Target = owner!;
        Active.Value = true;
        Target.Target = target;
        HandleTargetChanged();
    }

    internal void DestroySelf()
    {
        Unwatch();
        WorldGizmos.For(World)?.UntrackComponent(Target?.RawTarget, this);
        Slot?.Destroy();
    }

    // Same rule as the slot gizmo: a target destroyed, parked by an undoable delete or cleared is gone,
    // the owner (or the authority, once the owner has left) takes the rig down after whatever reported
    // it, and everyone else only hides it here until that removal lands. -xlinka
    private Component? _parkedComponent;
    private bool _targetGone;
    private bool _removalQueued;
    private int _hierarchyEpoch = -1;

    private void LoseTarget()
    {
        var target = _watchedComponent;
        Unwatch();
        WorldGizmos.For(World)?.UntrackComponent(target, this);
        _parkedComponent = target is { IsDestroyed: false, Slot: { IsDestroyed: false } } ? target : null;
        OnTargetGone();
    }

    private void OnTargetGone()
    {
        if (IsDestroyed)
            return;
        _targetGone = true;
        Slot?.SetLocallyHidden(true);
        if (MayTakeDown())
            QueueRemoval();
    }

    private void ShowAgain()
    {
        if (!_targetGone)
            return;
        _targetGone = false;
        Slot?.SetLocallyHidden(false);
    }

    private bool MayTakeDown()
    {
        var owner = Owner?.Target;
        return owner != null ? ReferenceEquals(owner, World?.LocalUser) : World?.IsAuthority == true;
    }

    private void QueueRemoval()
    {
        if (_removalQueued || World == null)
            return;
        _removalQueued = true;
        World.RunSynchronously(() =>
        {
            _removalQueued = false;
            if (IsDestroyed || !_targetGone || !MayTakeDown())
                return;
            using (Owner?.Target == null ? World?.DataModelPermissions?.EnterSystemBypass() : null)
                DestroySelf();
        });
    }

    // A parking delete only moves the hierarchy, so this is the one thing here that runs per frame: an
    // int compare until something somewhere is reparented.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        int epoch = Slot.HierarchyEpoch;
        if (epoch == _hierarchyEpoch)
            return;
        _hierarchyEpoch = epoch;

        var slot = _watchedSlot;
        if (slot != null)
        {
            if (slot.IsDestroyed || UndoGraveyard.Holds(slot))
                LoseTarget();
            return;
        }
        var parked = _parkedComponent;
        if (parked == null)
            return;
        if (parked.IsDestroyed || parked.Slot is not { IsDestroyed: false } parkedSlot)
            _parkedComponent = null;
        else if (!UndoGraveyard.Holds(parkedSlot))
        {
            _parkedComponent = null;
            HandleTargetChanged();
        }
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Target.OnTargetChange += _ => HandleTargetChanged();
        Owner.OnTargetChange += _ => WorldGizmos.For(World)?.ReindexComponentOwner(this);
        // The pads are separate slots, so hiding the gizmo has to take them with it.
        Active.OnChanged += _ =>
        {
            RunLayoutHandles();
            NotifyChanged();
        };
    }

    public override void OnStart()
    {
        base.OnStart();
        // A gizmo that arrived over the wire never ran Setup, and its target ref may only have
        // resolved after the component landed.
        HandleTargetChanged();
        if (Slot != null)
            Slot.SubtreeStructureChanged += OnOwnStructureChanged;
    }

    // The shape slot may land after this component did, on a peer that only received the gizmo.
    private void OnOwnStructureChanged(Slot slot)
    {
        if (_shapeRoot is { IsRemoved: false })
            return;
        if (!EnsureShapeRoot())
            return;
        FollowTarget();
        NotifyChanged();
    }

    public override void OnUserLeft(User user)
    {
        // Only the authority prunes: every peer sees the departure, and a peer that tried would just
        // have the delete refused. RawTarget because the leaving user's ref already reads null.
        if (World is { IsAuthority: true } world && user != null && ReferenceEquals(user, Owner?.RawTarget))
        {
            using (world.DataModelPermissions?.EnterSystemBypass())
                DestroySelf();
        }
    }

    public override void OnDestroy()
    {
        if (Slot != null)
            Slot.SubtreeStructureChanged -= OnOwnStructureChanged;
        Unwatch();
        WorldGizmos.For(World)?.UntrackComponent(Target?.RawTarget, this);
        base.OnDestroy();
    }

    private void HandleTargetChanged()
    {
        var target = TargetComponent;
        if (ReferenceEquals(target, _watchedComponent))
            return;

        var previous = _watchedComponent;
        Unwatch();
        var registry = WorldGizmos.For(World);
        if (previous != null)
            registry?.UntrackComponent(previous, this);
        _parkedComponent = null;

        if (target == null)
        {
            _wire.Clear();
            _wireVersion++;
            if (previous != null)
                OnTargetGone();
            NotifyChanged();
            return;
        }
        if (target.Slot is { } targetSlot && UndoGraveyard.Holds(targetSlot))
        {
            _parkedComponent = target;
            OnTargetGone();
            return;
        }

        ShowAgain();
        Watch(target);
        registry?.TrackComponent(target, this);
        EnsureShapeRoot();
        if (!_handlesBuilt && _buildsChrome)
        {
            BuildHandles();
            _handlesBuilt = true;
        }
        RebuildShape();
        FollowTarget();
    }

    // WATCHES: every source that can change the shape or the pose, and nothing else. A gizmo whose
    // target sits still and is not being edited receives no callbacks at all.
    private void Watch(Component target)
    {
        _watchedComponent = target;
        target.Changed += OnTargetChanged;
        WatchShapeFields();

        // Lifetime hangs off the component's OWN slot; the pose can come from a different one (a
        // plunger's travel is authored in its parent's space, not its own).
        var slot = target.Slot;
        if (slot != null && !slot.IsDestroyed)
        {
            _watchedSlot = slot;
            slot.OnPrepareDestroy += OnTargetSlotDestroyed;
            slot.OnComponentRemoved += OnSlotComponentRemoved;
            slot.ParentChanged += OnTargetSlotMoved;
            slot.ActiveChanged += OnTargetSlotMoved;
        }

        var follow = FollowSlot;
        if (follow == null || follow.IsDestroyed)
            follow = slot;
        if (follow == null || follow.IsDestroyed)
            return;
        _followSlot = follow;
        follow.WorldTransformChanged += OnTargetTransformChanged;
    }

    private void WatchShapeFields()
    {
        UnwatchShapeFields();
        if (_watchedComponent == null)
            return;
        CollectShapeFields(_watched);
        for (int i = 0; i < _watched.Count; i++)
        {
            if (_watched[i] != null)
                _watched[i].Changed += OnShapeFieldChanged;
        }
    }

    private void UnwatchShapeFields()
    {
        for (int i = 0; i < _watched.Count; i++)
        {
            if (_watched[i] != null)
                _watched[i].Changed -= OnShapeFieldChanged;
        }
        _watched.Clear();
    }

    private void Unwatch()
    {
        if (_watchedComponent != null)
            _watchedComponent.Changed -= OnTargetChanged;
        _watchedComponent = null;
        UnwatchShapeFields();

        if (_watchedSlot != null)
        {
            _watchedSlot.OnPrepareDestroy -= OnTargetSlotDestroyed;
            _watchedSlot.OnComponentRemoved -= OnSlotComponentRemoved;
            _watchedSlot.ParentChanged -= OnTargetSlotMoved;
            _watchedSlot.ActiveChanged -= OnTargetSlotMoved;
        }
        _watchedSlot = null;

        if (_followSlot != null)
            _followSlot.WorldTransformChanged -= OnTargetTransformChanged;
        _followSlot = null;
    }

    // The component's own coalesced change covers what the per-field watches cannot name: a target
    // whose extent comes from a REFERENCE (a mesh collider's mesh, a renderer's mesh) rather than a
    // number, and the enabled flag.
    private void OnTargetChanged(IChangeable changed) => MarkShapeDirty();

    private void OnShapeFieldChanged(IChangeable changed) => MarkShapeDirty();

    private void OnTargetTransformChanged(Slot slot) => FollowTarget();

    private void OnTargetSlotDestroyed(Slot slot) => LoseTarget();

    private void OnTargetSlotMoved(Slot slot)
    {
        if (UndoGraveyard.Holds(slot))
            LoseTarget();
    }

    private void OnSlotComponentRemoved(Slot slot, Component component)
    {
        if (ReferenceEquals(component, _watchedComponent))
            LoseTarget();
    }

    // deferred and coalesced: a field change can arrive mid-decode, and an edit writing four fields at
    // once must still rebuild once
    protected void MarkShapeDirty()
    {
        _shapeDirty = true;
        if (_rebuildQueued)
            return;
        _rebuildQueued = true;
        World?.RunSynchronously(() =>
        {
            _rebuildQueued = false;
            if (!IsDestroyed && _shapeDirty)
                RebuildShape();
        });
    }

    private void RebuildShape()
    {
        _shapeDirty = false;
        if (TargetComponent == null)
            return;

        // A gizmo whose extent comes from something the target only REFERENCES (a collider's mesh) has
        // to re-aim its watches when that reference is repointed, or the shape stops tracking the new
        // one. Everything else keeps the set it collected when the target resolved.
        if (ShapeFieldsVary)
            WatchShapeFields();

        _wire.Clear();
        _wire.Tint = BaseTint;
        BuildWire(_wire);
        _wireVersion++;

        RunLayoutHandles();
        NotifyChanged();
    }

    // FOLLOW: the gizmo slot carries the target's pose, a child slot carries its scale. Two writers on
    // one node fight, so nothing downstream of here touches the gizmo slot's transform.
    private void FollowTarget()
    {
        // A rig destroyed this frame keeps its watches until its OnDestroy, later in the same update.
        var slot = _followSlot;
        var gizmoSlot = Slot;
        if (slot == null || slot.IsDestroyed || gizmoSlot == null || gizmoSlot.IsRemoved || IsDestroyed)
            return;

        var position = slot.GlobalPosition;
        var rotation = slot.GlobalRotation;
        if (!Approximately(position, _followPosition))
        {
            _followPosition = position;
            gizmoSlot.GlobalPosition = position;
        }
        if (!Approximately(rotation, _followRotation))
        {
            _followRotation = rotation;
            gizmoSlot.GlobalRotation = rotation;
        }

        var scale = ShapeUsesTargetScale ? slot.GlobalScale : float3.One;
        if (Approximately(scale, _followScale))
            return;
        // Resolve the slot BEFORE recording the value: a first resolve forces the guard open again, and
        // recording first would have that reset thrown away.
        if (!EnsureShapeRoot())
            return;
        _followScale = scale;
        _shapeRoot!.LocalScale.Value = scale;
        // Pads sit at extents measured in component-local units, so a scale change moves all of them.
        RunLayoutHandles();
    }

    private bool EnsureShapeRoot()
    {
        if (_shapeRoot is { IsRemoved: false, IsDestroyed: false })
            return true;
        var gizmoSlot = Slot;
        if (gizmoSlot == null || gizmoSlot.IsRemoved)
            return false;

        _shapeRoot = gizmoSlot.FindChild("Shape", recursive: false);
        if (_shapeRoot == null)
        {
            if (!_buildsChrome)
                return false; // still in flight from the peer that built it
            _shapeRoot = gizmoSlot.AddSlot("Shape");
            _shapeRoot.Persistent.Value = false;
        }
        // Force the first write through: whatever the slot carries now is not what this target needs.
        _followScale = new float3(float.NaN, 1f, 1f);
        return true;
    }

    // SUBCLASS SURFACE

    // subclasses override to stay distinguishable when several are up at once
    protected virtual color BaseTint => new(0.35f, 0.95f, 0.85f, 0.9f);

    // subscribed individually, which is what keeps the gizmo silent while nothing is being edited
    protected abstract void CollectShapeFields(List<IChangeable> fields);

    // true for anything reading through a reference, where the field set can change while the target
    // stays the same
    protected virtual bool ShapeFieldsVary => false;

    // emits in the target component's local space (before its global scale); the shape root applies the scale
    protected abstract void BuildWire(GizmoWireBuilder wire);

    // called once, when the target is first resolved
    protected virtual void BuildHandles() { }

    // called after every re-emit and after a scale change, and only on the peer that owns the rig
    protected virtual void LayoutHandles() { }

    // ownership-gated, not local: pad positions are datamodel writes that replicate, so a peer that only
    // RECEIVED this gizmo laying them out too would be a second writer on the same slots, and a layout
    // that grows its pad set (a LOD gizmo picking up a new level) would build a duplicate rig on every peer
    private void RunLayoutHandles()
    {
        if (!_buildsChrome)
            return;
        LayoutHandles();
    }

    protected T? TargetAs<T>() where T : Component => TargetComponent as T;

    // false for the handful of components whose numbers are already world metres (a LOD switch
    // distance), where re-applying the scale would draw the ring in the wrong place and a non-uniform
    // scale would turn a sphere into an ellipsoid
    protected virtual bool ShapeUsesTargetScale => true;

    // one for gizmos that opt out of the scale mirror
    protected float3 TargetScale
        => ShapeUsesTargetScale ? _followSlot?.GlobalScale ?? float3.One : float3.One;

    // defaults to the target's own slot; overridden by the few components whose extent is authored in
    // some other slot's space
    protected virtual Slot? FollowSlot => TargetComponent?.Slot;

    // HANDLES

    protected IReadOnlyList<ExtentHandle> Handles => _handles;

    // pad sits at value * distancePerUnit along the axis, so a radius uses 1 and a box half-extent 0.5
    protected ExtentHandle AddHandle(string name, in float3 axis, IField<float>? field, float distancePerUnit,
        GizmoMaterialKind material, float minimum = 0f, float maximum = float.MaxValue)
    {
        var handle = CreateHandle(name, axis, distancePerUnit, material, minimum, maximum);
        handle.FloatField.Target = field!;
        return handle;
    }

    protected ExtentHandle AddHandle(string name, in float3 axis, IField<float3>? field, int vectorAxis,
        float distancePerUnit, GizmoMaterialKind material, float minimum = 0f, float maximum = float.MaxValue)
    {
        var handle = CreateHandle(name, axis, distancePerUnit, material, minimum, maximum);
        handle.VectorField.Target = field!;
        handle.VectorAxis.Value = vectorAxis;
        return handle;
    }

    private ExtentHandle CreateHandle(string name, in float3 axis, float distancePerUnit,
        GizmoMaterialKind material, float minimum, float maximum)
    {
        var gizmoSlot = Slot;
        var host = gizmoSlot.FindChild(name, recursive: false) ?? gizmoSlot.AddSlot(name);
        host.Persistent.Value = false;

        var handle = host.GetComponent<ExtentHandle>() ?? host.AttachComponent<ExtentHandle>();
        handle.TargetSlot.Target = (_followSlot ?? _watchedSlot)!;
        handle.AxisReference.Target = gizmoSlot;
        handle.LocalAxis.Value = axis;
        handle.DistancePerUnit.Value = distancePerUnit;
        handle.MinValue.Value = minimum;
        handle.MaxValue.Value = maximum;
        handle.SetPickBox(float3.Zero, float3.One * (PadSize * 1.6f));

        if (host.GetComponent<MeshRenderer>() == null)
        {
            var mesh = host.AttachComponent<Meshes.BoxMesh>();
            mesh.Size.Value = float3.One * PadSize;
            var renderer = host.AttachComponent<MeshRenderer>();
            renderer.Mesh.Target = mesh;
            renderer.ShadowCastMode.Value = ShadowCastMode.Off;
            var shared = WorldGizmos.For(World)?.GetMaterial(World, material);
            if (shared != null)
                renderer.Material.Target = shared;
        }

        // Named lookup, so a gizmo whose pad set grows later (a LOD level being added) can call this
        // again for the same name without stacking duplicates in the list.
        if (!_handles.Contains(handle))
            _handles.Add(handle);
        return handle;
    }

    // point measured in the target component's local space; also tells the pad how many world units one
    // unit of its field is worth so the drag maths stays scale-correct
    protected void PlaceHandle(ExtentHandle? handle, in float3 localPoint)
    {
        if (handle == null || handle.IsDestroyed)
            return;
        var host = handle.Slot;
        if (host == null || host.IsRemoved)
            return;

        var scale = TargetScale;
        host.LocalPosition.Value = new float3(localPoint.x * scale.x, localPoint.y * scale.y, localPoint.z * scale.z);
        host.ActiveSelf.Value = IsVisible;

        // Distance along the axis is scaled by whatever the axis picks up from the target's scale, so
        // the pad's drag has to divide by the same thing or a scaled object edits at the wrong rate.
        var axis = handle.LocalAxis.Value;
        float axisScale = MathF.Abs(axis.x) * scale.x + MathF.Abs(axis.y) * scale.y + MathF.Abs(axis.z) * scale.z;
        float length = MathF.Sqrt(axis.LengthSquared);
        if (length > 1e-6f)
            axisScale /= length;
        handle.DragScale.Value = handle.DistancePerUnit.Value * MathF.Max(axisScale, 1e-4f);
    }

    // e.g. a spot-angle pad on a point light
    protected static void HideHandle(ExtentHandle? handle)
    {
        var host = handle?.Slot;
        if (host != null && !host.IsRemoved && host.ActiveSelf.Value)
            host.ActiveSelf.Value = false;
    }

    public void NotifyHandleDrag(bool dragging)
    {
        int previous = _draggingHandles;
        _draggingHandles = System.Math.Max(0, dragging ? previous + 1 : previous - 1);
        if ((previous > 0) != (_draggingHandles > 0))
            NotifyChanged();
    }

    // pads do not distance-scale: they have to sit exactly ON the extent they edit, so moving them for
    // legibility would lie about the value
    protected const float PadSize = 0.03f;

    private static bool Approximately(in float3 a, in float3 b)
        => MathF.Abs(a.x - b.x) < 1e-5f && MathF.Abs(a.y - b.y) < 1e-5f && MathF.Abs(a.z - b.z) < 1e-5f;

    private static bool Approximately(in floatQ a, in floatQ b)
        => MathF.Abs(a.x - b.x) < 1e-5f && MathF.Abs(a.y - b.y) < 1e-5f
        && MathF.Abs(a.z - b.z) < 1e-5f && MathF.Abs(a.w - b.w) < 1e-5f;
}
