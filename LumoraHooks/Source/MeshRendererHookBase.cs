// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using LumoraMeshes = Lumora.Core.Components.Meshes;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

public abstract class MeshRendererHookBase<T, U> : ComponentHook<T>, ILodRangeTarget, IDrawnInstance, IPlacedRenderer
    where T : MeshRenderer
    where U : Node3D
{
    protected struct RendererData
    {
        public Node3D gameObject;
        public MeshInstance3D meshInstance;
        public U meshRenderer;
    }

    private MeshInstance3D meshInstance = null!;
    private int _lastSurfaceOverrideCount;

    // Unbounded UI ordering (Owner.PerSurfaceOrdering): one MeshInstance3D per mesh surface, each ordered by a
    // distinct SortingOffset so draw order isn't capped by Godot's 256-level render_priority. -xlinka
    private readonly System.Collections.Generic.List<MeshInstance3D> _perSurfaceInstances = new();
    private const float PerSurfaceSortStride = Lumora.Core.Components.MeshRenderer.PerSurfaceSortStride;

    protected abstract bool UseMeshInstance { get; }

    // The instance is placed from the slot's world transform (Place), never by the node it hangs under.
    protected override bool ReadsNodePose => !UseMeshInstance;

    protected bool meshWasChanged { get; private set; }

    public U MeshRenderer { get; protected set; } = null!;

    protected abstract void AssignMesh(U renderer, Mesh mesh);

    protected virtual void OnAttachRenderer()
    {
    }

    protected virtual void OnCleanupRenderer()
    {
    }

    // RenderLayerOverrideHook only walks nodes that exist when it applies, so mesh
    // instances created later (canvas chunks built after a parked dash reactivates)
    // would stay on the default layer - invisible to layer-masked capture cameras.
    // Inherit the override at creation instead.
    private void ApplyInheritedRenderLayer(VisualInstance3D visual)
    {
        // A view band (MeshRenderer.ViewLayers) is this renderer's own and outranks anything above it.
        if (Owner.ViewLayers != 0)
        {
            visual.Layers = Helpers.RenderHelper.ForWorld(Owner.World, (uint)Owner.ViewLayers);
            return;
        }
        uint layers = visual.Layers;
        for (var slot = Owner.Slot; slot != null; slot = slot.Parent)
        {
            var layerOverride = slot.GetComponent<RenderLayerOverride>();
            if (layerOverride != null && layerOverride.Enabled && layerOverride.Layer.Value != 0)
            {
                layers = (uint)layerOverride.Layer.Value;
                break;
            }
        }
        visual.Layers = Helpers.RenderHelper.ForWorld(Owner.World, layers);
    }

    public override void ApplyChanges()
    {
        // Not initialized yet (or already torn down) - nothing to attach under.
        if (attachedNode == null)
        {
            return;
        }

        if (Owner.Mesh.Target != null && IsMeshAssetAvailable())
        {
            if (MeshRenderer == null)
            {
                if (UseMeshInstance)
                {
                    // The instance hangs under the slot's node, so the node's visibility, the layer walks and the
                    // capture scans all still find it, but it is top-level and placed from the slot's world
                    // transform: no node above it is moved for it, and a slot carrying only renderers leaves its
                    // node where it is. One node a renderer instead of a holder and an instance. -xlinka
                    meshInstance = new MeshInstance3D();
                    meshInstance.Name = "MeshRenderer";
                    meshInstance.TopLevel = true;
                    ApplyInheritedRenderLayer(meshInstance);
                    attachedNode.AddChild(meshInstance);
                    MeshRenderer = (meshInstance as U)!;
                    _rangeApplied = false;
                    _instanceParamsStale = true;
                    WatchSlot(true);
                    Place(true);
                }
                else
                {
                    var gameObject = new Node3D();
                    gameObject.Name = "MeshRenderer";
                    attachedNode.AddChild(gameObject);
                    MeshRenderer = (gameObject as U)!;
                }
                OnAttachRenderer();
            }
            else if (_placeStale && Owner.Slot.IsActive)
            {
                Place(true);
            }

            // On a joiner this hook often drains BEFORE its mesh provider has uploaded geometry, so the
            // very first apply latches a 0-surface (or null) mesh. A plain SyncRef<Component> never
            // re-fires when the provider's mesh DATA later appears, so without re-pulling we'd stay
            // invisible forever. Re-pull whenever the ref changed OR we're still holding an empty mesh -
            // so the provider's post-upload FlagSurfacesDirty re-drive (and any later regenerate) actually
            // re-reads the now-populated ArrayMesh. Once we hold a real surface-bearing mesh this stops. -xlinka
            bool meshRefChanged = Owner.Mesh.GetWasChangedAndClear();
            bool haveRealMesh = meshInstance?.Mesh is ArrayMesh curMesh && curMesh.GetSurfaceCount() > 0;
            meshWasChanged = meshRefChanged || !haveRealMesh || _meshSourceReplaced;
            if (meshWasChanged)
            {
                _meshSourceReplaced = false;

                _surfaceSource = null;
                var godotMesh = GetGodotMeshFromAsset();
                if (UseMeshInstance && meshInstance != null)
                {
                    meshInstance.Mesh = godotMesh;
                }
                else
                {
                    AssignMesh(MeshRenderer, godotMesh);
                }

                // COME BACK UNTIL THE MESH ACTUALLY ARRIVES.
                //
                // An imported mesh decodes off-thread and uploads on a later frame, so the first apply
                // asks a MeshProvider for geometry that is not there yet and gets null. ApplyChanges is
                // queue-driven and an asset finishing its decode dirties NOTHING, so that null was
                // final: the renderer sat with an empty MeshInstance3D for the rest of the session.
                // Skinned meshes have carried their own requeue for a while, which is why an imported
                // avatar showed its body and its hair and none of its props - every one of those is a
                // plain MeshRenderer.
                //
                // Bounded, because a genuinely broken asset must not spin forever. The cap is loose
                // enough that no honest decode reaches it, and hitting it says so once. -xlinka
                if (godotMesh == null && Owner.Mesh.Target != null && Owner is { IsDestroyed: false })
                {
                    // A FRAME COUNT WAS THE WRONG CLOCK. Sixty meshes decoding at once take longer
                    // than ten seconds, so the cap fired on a load that was progressing perfectly well
                    // and the prop then only appeared if something unrelated happened to re-dirty the
                    // hook. The provider already knows whether anything is still coming - ask it, and
                    // only start counting against the cap once it says no. -xlinka
                    bool stillArriving = Owner.Mesh.Target is MeshProvider pending && pending.IsLoadPending;

                    if (stillArriving || _meshWaitFrames < MeshWaitFrameLimit)
                    {
                        if (!stillArriving)
                            _meshWaitFrames++;
                        Owner.MarkChangeDirty();
                    }
                    else if (!_meshWaitGaveUp)
                    {
                        _meshWaitGaveUp = true;
                        LumoraLogger.Warn($"MeshRendererHook '{Owner.Slot?.SlotName.Value}': mesh "
                                        + $"{Owner.Mesh.Target.GetType().Name} never produced geometry after "
                                        + $"{MeshWaitFrameLimit} frames with nothing still loading; "
                                        + "the renderer stays empty.");
                    }
                }
                else if (godotMesh != null)
                {
                    _meshWaitFrames = 0;
                    _meshWaitGaveUp = false;
                }
            }

            ApplyBoundsOverride();

            // A renderer with geometry and NO material must not draw.
            //
            // We hand Godot a null surface override when the list is empty and it quietly substitutes its
            // own default white-grey, so the object shows up as a big pale untextured blob instead of not
            // showing up at all. On an imported avatar whose material reference failed to wire, that blob
            // follows the wearer into every world and looks like a bug in something else entirely.
            // Drawing nothing is the honest result of having nothing to draw it with. -xlinka
            // COUNT is not the question, CONTENT is. A material list can now carry empty slots on
            // purpose - the importer leaves one where a material failed to build so the surfaces after
            // it keep their index - and a list of nothing but holes still draws the grey default this
            // check exists to prevent.
            // A soft body on the same slot draws its deformed copy of this mesh itself; see
            // MeshRenderer.SetLocallyHiddenMesh.
            ApplyVisible();

            // See MeshRenderer.ExtraCullMargin: vertex-shader-displaced UI needs grown cull bounds.
            if (meshInstance != null && meshInstance.ExtraCullMargin != Owner.ExtraCullMargin)
            {
                meshInstance.ExtraCullMargin = Owner.ExtraCullMargin;
            }

            // A band given back returns the instances to the public layer and whatever they inherit over it.
            if (Owner.ViewLayers != _viewLayersApplied)
            {
                _viewLayersApplied = Owner.ViewLayers;
                if (meshInstance != null)
                {
                    meshInstance.Layers = (uint)Helpers.RenderHelper.PUBLIC_LAYER;
                    ApplyInheritedRenderLayer(meshInstance);
                }
                foreach (var surface in _perSurfaceInstances)
                {
                    if (!GodotObject.IsInstanceValid(surface))
                        continue;
                    surface.Layers = (uint)Helpers.RenderHelper.PUBLIC_LAYER;
                    ApplyInheritedRenderLayer(surface);
                }
            }

            bool renderQueueApplied = false;
            if (Owner.SortingOrder.GetWasChangedAndClear())
            {
                ApplyRenderQueue();
                renderQueueApplied = true;
            }

            if (Owner.ShadowCastMode.GetWasChangedAndClear())
            {
                ApplyShadowCastMode(Owner.ShadowCastMode.Value);
            }

            bool materialRefsChanged = Owner.MaterialsChanged
                || Owner.MaterialPropertyBlocksChanged
                || AnyChanged(Owner.Materials)
                || AnyChanged(Owner.MaterialPropertyBlocks);
            bool surfacePrioritiesChanged = Owner.SurfaceRenderPrioritiesChanged
                || AnyChanged(Owner.SurfaceRenderPriorities);
            bool surfaceAssignmentsChanged = Owner.SurfaceAssignmentsChanged || meshWasChanged;
            bool overlayChanged = Owner.OverlayOrderChanged;
            bool materialsChanged = _surfaceStillLoading || materialRefsChanged || surfacePrioritiesChanged || surfaceAssignmentsChanged
                || overlayChanged;

            if (materialsChanged)
            {
                Owner.MaterialsChanged = false;
                Owner.MaterialPropertyBlocksChanged = false;
                Owner.SurfaceRenderPrioritiesChanged = false;
                Owner.SurfaceAssignmentsChanged = false;
                Owner.OverlayOrderChanged = false;
                ApplyMaterials();
                if (!renderQueueApplied)
                    ApplyRenderQueue();
                _instanceParamsStale = true;
                ApplyVisible();
            }

            ApplyInstanceParams();
            SyncPerSurfaceInstances();
            ApplyLodRange();
            ReportOnce();
        }
        else
        {
            // SILENCE IS NOT AN ANSWER. This branch tears the renderer down and used to say nothing at
            // all, so a renderer that never got past the gate was indistinguishable from one that was
            // never created - and that is exactly the state every imported prop was in. -xlinka
            ReportMissing();
            CleanupRenderer(destroyingWorld: false);
        }

        BatchedBy?.RendererChanged();
    }

    private static readonly global::Godot.Collections.Array<global::Godot.Collections.Array> SplitNoBlendShapes = new();
    private static readonly global::Godot.Collections.Dictionary SplitNoLods = new();

    // See MeshRenderer.SetInstanceParams. Written again on a fresh instance and after a material swap, since
    // the platform files an instance value against the material it wears.
    private static readonly StringName InstanceParamsName = "instance_params";
    private bool _instanceParamsStale = true;

    private void ApplyInstanceParams()
    {
        if (!Owner.HasInstanceParams || meshInstance == null || (!Owner.InstanceParamsChanged && !_instanceParamsStale))
            return;
        Owner.InstanceParamsChanged = false;
        _instanceParamsStale = false;
        var value = Owner.InstanceParams;
        meshInstance.SetInstanceShaderParameter(InstanceParamsName, new Vector4(value.x, value.y, value.z, value.w));
    }

    private int _missingApplies;
    private bool _missingReported;

    // Named after a short grace, not on the first pass: a mesh reference is wired in a second pass
    // after the components are built, so "no mesh yet" is normal for the first few frames and only
    // means something once it has stopped being temporary.
    private void ReportMissing()
    {
        if (_missingReported || _reported)
            return;
        if (++_missingApplies < MissingGraceApplies)
        {
            // Keep coming back, or the counter never advances: nothing else re-drives a hook that
            // took the empty branch, which is the same trap the mesh wait above was in.
            if (Owner is { IsDestroyed: false })
                Owner.MarkChangeDirty();
            return;
        }

        _missingReported = true;
        var target = Owner.Mesh.Target;
        LumoraLogger.Warn($"MeshRendererHook '{Owner.Slot?.SlotName.Value}': NO RENDERER after "
                        + $"{MissingGraceApplies} applies - Mesh.Target={(target == null ? "null" : target.GetType().Name)}, "
                        + $"assetAvailable={IsMeshAssetAvailable()}, materials={Owner.Materials.Count}, "
                        + $"enabled={Owner.Enabled}, slotActive={Owner.Slot?.IsActive}");
    }

    // LOD plumbing. A LodGroup decides the band; this only makes sure every instance the hook owns
    // carries it, including per-surface ones that get built after the group last spoke. -xlinka
    private LodVisibilityRange _lodRange = LodVisibilityRange.Unbounded;

    // The band last written to the instances, so a re-apply that changed nothing about it (a mesh
    // re-upload, a material swap) does not push five visibility properties through the marshalling
    // layer again. Cleared whenever an instance is created, since a fresh one holds engine defaults.
    private LodVisibilityRange _appliedRange = LodVisibilityRange.Unbounded;
    private bool _rangeApplied;

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        if (_lodRange.Equals(range))
            return;
        _lodRange = range;
        ApplyLodRange();
        BatchedBy?.RendererChanged();
    }

    public bool TryGetLargestDimension(out float size)
    {
        size = 0f;
        if (meshInstance == null || meshInstance.Mesh == null)
            return false;
        var aabb = meshInstance.GetAabb();
        float scale = LargestScale(_hasPlaced ? _placed.Basis : meshInstance.GlobalTransform.Basis);
        size = Mathf.Max(aabb.Size.X, Mathf.Max(aabb.Size.Y, aabb.Size.Z)) * scale;
        return size > 0f;
    }

    private void ApplyLodRange()
    {
        // Torn down: a LOD group handing back an unbounded band on its way out can reach us after the
        // component is gone, and there is nothing left to read a view distance off.
        if (Owner == null)
            return;

        var effective = LodVisibilityRange.Tightest(
            in _lodRange,
            LodVisibilityRange.To(Owner.MaxViewDistance, Owner.ViewDistanceFadeMargin));
        if (effective.Equals(LodVisibilityRange.Unbounded))
            effective = AutoRange();
        if (_rangeApplied && _appliedRange.Equals(effective))
            return;
        _appliedRange = effective;
        _rangeApplied = true;
        effective.ApplyTo(meshInstance);
        foreach (var inst in _perSurfaceInstances)
            effective.ApplyTo(inst);
    }

    // NOTHING TO SEE PAST A PIXEL. A renderer nobody gave a distance (no view distance of its own, no LOD band)
    // stops drawing where it would cover about a pixel: its bounding sphere under a two-thousandth of the distance
    // to it, which is a pixel at 1440p over a 75 degree view and about one and a half in a headset. Never on a
    // person, a panel or a private interface, nor anything drawn on a band or in an order of its own. -xlinka
    private const float AutoRangeRatio = 2000f;
    private float _autoRangeScale;
    private Aabb _customBounds;

    private LodVisibilityRange AutoRange()
    {
        _autoRangeScale = 0f;
        if (meshInstance == null || meshInstance.Mesh is not { } mesh || !_hasPlaced)
            return LodVisibilityRange.Unbounded;
        var slot = Owner.Slot;
        if (slot == null || slot.ActiveUserRoot != null || Owner.World is not { IsUserspace: false }
            || Owner.Mesh.Target is Helio.UI.LocalMeshSource || Owner.PerSurfaceOrdering || Owner.HasOverlayOrder
            || Owner.ViewLayers != 0)
            return LodVisibilityRange.Unbounded;
        var bounds = _customBounds.Size != Vector3.Zero ? _customBounds : mesh.GetAabb();
        float scale = LargestScale(_placed.Basis);
        float radius = bounds.Size.Length() * 0.5f * scale;
        if (!(radius > 0f) || float.IsInfinity(radius))
            return LodVisibilityRange.Unbounded;
        _autoRangeScale = scale;
        return LodVisibilityRange.To(radius * AutoRangeRatio + radius, 0f);
    }

    private static float LargestScale(in Basis basis)
        => Mathf.Max(basis.X.Length(), Mathf.Max(basis.Y.Length(), basis.Z.Length()));

    // Unbounded UI ordering. When Owner.PerSurfaceOrdering is set (Helio's opt-in mode), render each surface of
    // the chunk mesh as its OWN MeshInstance3D with a distinct SortingOffset (= SortingOrder * stride + surface
    // index) and a uniform render_priority. Godot then orders them by SortingOffset (full range) instead of the
    // 256-level render_priority cap, so UI layers without bound. The combined instance is hidden while this is
    // active; turning the mode off frees the per-surface instances and restores it. Cost: a single-surface mesh
    // + a material duplicate per surface per update, so it's a deliberate opt-in. -xlinka
    private void SyncPerSurfaceInstances()
    {
        if (!Owner.PerSurfaceOrdering)
        {
            if (_perSurfaceInstances.Count > 0)
            {
                foreach (var inst in _perSurfaceInstances)
                    if (GodotObject.IsInstanceValid(inst)) inst.QueueFree();
                _perSurfaceInstances.Clear();
                if (meshInstance != null) meshInstance.Visible = Owner.Enabled;
            }
            return;
        }

        if (meshInstance == null || meshInstance.Mesh is not ArrayMesh combined) return;

        int surfaceCount = combined.GetSurfaceCount();
        meshInstance.Visible = false; // the per-surface instances render in its place

        var parent = meshInstance.GetParent();
        float baseKey = Owner.SortingOrder.Value * PerSurfaceSortStride;
        bool enabled = Owner.Enabled;

        for (int i = 0; i < surfaceCount; i++)
        {
            MeshInstance3D inst;
            if (i < _perSurfaceInstances.Count)
            {
                inst = _perSurfaceInstances[i];
            }
            else
            {
                inst = new MeshInstance3D();
                inst.TopLevel = true;
                inst.IgnoreOcclusionCulling = !_occludable;
                ApplyInheritedRenderLayer(inst);
                parent?.AddChild(inst);
                _perSurfaceInstances.Add(inst);
                _rangeApplied = false;
                if (_hasPlaced)
                    inst.Transform = _placed;
            }

            // Held across the native add: a temporary the binding is handed can be finalized mid-call. -xlinka
            var single = new ArrayMesh();
            var surfaceArrays = combined.SurfaceGetArrays(i);
            single.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surfaceArrays, SplitNoBlendShapes, SplitNoLods);
            System.GC.KeepAlive(surfaceArrays);
            inst.Mesh = single;

            // Duplicate so forcing a uniform render_priority doesn't mutate the chunk's shared/cloned material -
            // order is carried entirely by SortingOffset now.
            var srcMat = GetSurfaceMaterial(i);
            if (srcMat != null)
            {
                var mat = CloneMaterial(srcMat);
                mat.RenderPriority = 0;
                inst.SetSurfaceOverrideMaterial(0, mat);
            }

            inst.SortingOffset = i == 0 && Owner.LeadSurfaceSortingOffset is float lead ? lead : baseKey + i;
            // Scrolled UI chunks displace verts in the vertex shader; grow the cull bounds or Godot culls the
            // instance at glancing angles/up close while displaced pixels should still be visible. The value
            // is in the mesh's own local units - see MeshRenderer.ExtraCullMargin. These instances render in
            // the combined one's place, so a margin that only reached the combined instance would do nothing
            // at all while this mode is on. -xlinka
            if (inst.ExtraCullMargin != Owner.ExtraCullMargin)
                inst.ExtraCullMargin = Owner.ExtraCullMargin;
            inst.Visible = enabled;
        }

        for (int i = _perSurfaceInstances.Count - 1; i >= surfaceCount; i--)
        {
            if (GodotObject.IsInstanceValid(_perSurfaceInstances[i]))
                _perSurfaceInstances[i].QueueFree();
            _perSurfaceInstances.RemoveAt(i);
        }
    }

    // The body drawing this renderer in a batch of moving objects, told whenever anything about how it draws may
    // have changed, so it takes the renderer out and looks again.
    internal RigidBodyHook? BatchedBy;

    // Whether a batch of moving objects can draw this renderer for it: nothing drawn per instance, no ordering of
    // its own, and every surface wearing a material that draws the same in a batch.
    internal bool CanBatch
    {
        get
        {
            if (meshInstance == null || _surfaceStillLoading || Owner.HasInstanceParams || Owner.HasOverlayOrder
                || Owner.PerSurfaceOrdering || _perSurfaceInstances.Count > 0 || Owner.ViewLayers != 0)
                return false;
            int count = GetSurfaceCount();
            for (int i = 0; i < count; i++)
            {
                int index = _surfaceSource?.SubmeshForSurface(i) ?? i;
                if (Owner.GetSurfaceRenderPriority(index) != Lumora.Core.Components.MeshRenderer.NoSurfaceRenderPriority)
                    return false;
                var block = GetPropertyBlockAsset(index);
                if (block != null && block.IsValid)
                    return false;
                if (GetMaterialAsset(index)?.Hook is not MaterialAssetHook material || !material.Batchable)
                    return false;
            }
            return count > 0;
        }
    }

    // The instance this renderer draws through, for a body that places it on the render server itself while it
    // moves (RigidBodyHook). None while it draws one instance per surface.
    internal bool TryGetDrawn(out MeshInstance3D instance)
    {
        instance = meshInstance;
        return instance != null && GodotObject.IsInstanceValid(instance)
            && !Owner.PerSurfaceOrdering && _perSurfaceInstances.Count == 0;
    }

    MeshInstance3D? IDrawnInstance.DrawnInstance
        => TryGetDrawn(out var instance) ? instance : null;

    // WHERE THE INSTANCES ARE DRAWN: the slot's world transform (SlotHook.TryGetDrawnTransform), written when the
    // slot or anything above it moves. Slot.WorldTransformChanged gathers every move of a frame into one pass before
    // the hooks flush, so a moved renderer costs one write of its own instance, and the nodes above it are left
    // alone unless something else reads them. -xlinka
    private Transform3D _placed;
    private bool _hasPlaced;
    private Lumora.Core.Slot? _watchedSlot;
    private System.Action<Lumora.Core.Slot>? _onSlotMoved;
    private System.Action<Lumora.Core.Slot>? _onSlotActive;

    // Moved while switched off, so nothing was written: placed when it comes back on. An avatar's spare outfits
    // ride along with every step it takes.
    private bool _placeStale;

    private void WatchSlot(bool on)
    {
        var slot = on ? Owner?.Slot : null;
        if (ReferenceEquals(slot, _watchedSlot))
            return;
        if (_watchedSlot != null && _onSlotMoved != null)
        {
            _watchedSlot.WorldTransformChanged -= _onSlotMoved;
            _watchedSlot.ActiveChanged -= _onSlotActive;
        }
        _watchedSlot = slot;
        if (slot != null)
        {
            _onSlotMoved ??= _ => Place(false);
            // Can come from the sync thread: only asks for an apply.
            _onSlotActive ??= _ =>
            {
                if (_placeStale)
                    Owner?.MarkChangeDirty();
            };
            slot.WorldTransformChanged += _onSlotMoved;
            slot.ActiveChanged += _onSlotActive;
        }
    }

    void IPlacedRenderer.Place(bool force) => Place(force);

    private void Place(bool force)
    {
        if (meshInstance == null || Owner?.Slot is not { IsDestroyed: false } slot)
            return;
        if (!force && !slot.IsActive)
        {
            _placeStale = true;
            return;
        }
        // Somebody else's avatar places its accessories on its own pose frames (AvatarPlacement).
        if (!force && AvatarPlacement.Defers(this, slot))
            return;
        if (!SlotHook.TryGetDrawnTransform(slot, out var transform))
            return;
        _placeStale = false;
        if (!force && _hasPlaced && transform == _placed)
            return;
        _placed = transform;
        _hasPlaced = true;
        // Always through the node, never the render server alone: the engine hands a node's own pose to the server
        // again whenever the node is shown or comes back into the tree, so a node left behind put a canvas chunk
        // back where it was the last time the node was written (world browser cards at their old place or gone).
        // -xlinka
        meshInstance.Transform = transform;
        foreach (var surface in _perSurfaceInstances)
            surface.Transform = transform;
        // A distance worked out from the size goes with the size.
        if (_autoRangeScale > 0f && LargestScale(transform.Basis) != _autoRangeScale)
            ApplyLodRange();
    }

    // Set by GetSurfaceMaterial whenever it hands back the placeholder, read once per apply.
    private bool _surfaceStillLoading;

    // The view band last put on the instances, so a change is applied once.
    private int _viewLayersApplied;

    private System.Collections.Generic.Dictionary<Material, Material> _overlayClones = new();
    private System.Collections.Generic.Dictionary<Material, Material> _overlayClonesUsed = new();
    private bool _overlayClonesDepthTest;

    protected virtual void ApplyMaterials()
    {
        if (meshInstance == null) return;

        int count = GetSurfaceCount();

        _surfaceStillLoading = false;
        _materialsApplied = true;
        bool overlay = Owner.HasOverlayOrder;
        // One overlay clone per source material, kept while the source stays the same. An apply used to
        // clone every surface every time it ran, and a text renderer or a scrolling list applies every
        // frame, so the dash was minting materials at frame rate for nothing. -xlinka
        if (!overlay || Owner.OverlayDepthTest != _overlayClonesDepthTest)
        {
            _overlayClones.Clear();
            _overlayClonesDepthTest = Owner.OverlayDepthTest;
        }
        _appliedMaterials.Clear();
        for (int i = 0; i < count; i++)
        {
            var material = GetSurfaceMaterial(i);
            if (overlay && material != null)
            {
                if (!_overlayClones.TryGetValue(material, out var clone))
                    clone = ToOverlayMaterial(material, Owner.OverlayDepthTest);
                _overlayClonesUsed[material] = clone;
                material = clone;
            }
            // A changed material property re-drives this renderer with the very same material objects.
            // Setting one again is not free: the platform re-files the instance and every point and spot
            // light it sits in renders its shadow again, so an animated material kept the lamps around
            // everything wearing it re-rendering every frame. -xlinka
            if (!ReferenceEquals(meshInstance.GetSurfaceOverrideMaterial(i), material))
                meshInstance.SetSurfaceOverrideMaterial(i, material);
            _appliedMaterials.Add(material);
        }
        // Only the sources this apply touched survive, so a renderer that swaps materials cannot grow
        // the cache without bound.
        (_overlayClones, _overlayClonesUsed) = (_overlayClonesUsed, _overlayClones);
        _overlayClonesUsed.Clear();

        // Come back until the real material lands.
        //
        // The comment on GetSurfaceMaterial says the swap "rides the asset-arrival notification that
        // already re-drives this hook". It does not: ApplyChanges is queue-driven, a texture finishing
        // its download dirties nothing, and so a surface that was still arriving at bind time kept the
        // checker for the rest of the session unless some unrelated edit re-dirtied the hook. Re-queue
        // while anything is still on the placeholder, and stop as soon as nothing is. -xlinka
        if (_surfaceStillLoading && Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();

        if (meshInstance.MaterialOverride != null)
            meshInstance.MaterialOverride = null;
        _lastSurfaceOverrideCount = count;
        ApplyOcclusionCulling(count);

        if (_uiDiagLogged < 3 && Owner.Slot?.Parent?.SlotName?.Value == "HelioTestPanel")
        {
            _uiDiagLogged++;
            int assigned = 0;
            for (int i = 0; i < count; i++)
            {
                if (meshInstance.GetSurfaceOverrideMaterial(i) != null) assigned++;
            }
            LumoraLogger.Debug(
                $"MeshRendererHook.ApplyMaterials[diag]: slot={Owner.Slot?.SlotName.Value} " +
                $"surfaces={count} materials(list)={Owner.Materials.Count} " +
                $"overrides(non-null)={assigned}");
        }
    }

    private int _uiDiagLogged;

    // Left undrawn behind a wall (StaticOccluders) only while every surface wears a material known to be tested
    // against depth: an overlay, a volume or a shader of the world's own may draw through the wall it stands
    // behind. Nothing on a person, in the private interface or in an order or band of its own. -xlinka
    private bool _occludable = true;

    private void ApplyOcclusionCulling(int count)
    {
        bool occludable = count > 0 && !Owner.HasOverlayOrder && Owner.ViewLayers == 0
            && Owner.Slot?.ActiveUserRoot == null && Owner.World is { IsUserspace: false };
        for (int i = 0; occludable && i < count; i++)
        {
            int index = _surfaceSource?.SubmeshForSurface(i) ?? i;
            occludable = GetMaterialAsset(index)?.Hook is MaterialAssetHook material && material.DepthTested;
        }
        if (occludable == _occludable || meshInstance == null)
            return;
        _occludable = occludable;
        meshInstance.IgnoreOcclusionCulling = !occludable;
        foreach (var surface in _perSurfaceInstances)
            surface.IgnoreOcclusionCulling = !occludable;
    }

    // The overlay band is decided per instance, so the ASSET material is never touched: a duplicate takes
    // the pass and priority changes and is dropped again when the band clears. A native material moves to
    // the transparent pass with the depth write kept, which is what lets its offset order it against the
    // dash. A shader material carries its pass in the shader source, so it only takes the priority: one
    // that already blends (the laser family) orders correctly, an opaque one stays where its shader put
    // it. -xlinka
    private static Material ToOverlayMaterial(Material source, bool depthTest)
    {
        if (source == null)
            return null!;

        var duplicate = CloneMaterial(source);
        if (duplicate is StandardMaterial3D standard)
        {
            if (standard.Transparency == BaseMaterial3D.TransparencyEnum.Disabled)
                standard.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            standard.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always;
            standard.NoDepthTest = !depthTest;
        }
        else if (duplicate is ShaderMaterial shaderMaterial && shaderMaterial.Shader is { } shader)
        {
            // A build still compiling lands in place: until then the clone wears the original shader, already
            // at the overlay priority.
            var transparent = OverlayShaders.For(shader, depthTest, built =>
            {
                if (GodotObject.IsInstanceValid(shaderMaterial) && ReferenceEquals(shaderMaterial.Shader, shader))
                    shaderMaterial.Shader = built;
            });
            if (transparent != null)
                shaderMaterial.Shader = transparent;
        }
        duplicate.RenderPriority = 127;
        return duplicate;
    }

    // NEVER Resource.Duplicate() A SHADER MATERIAL ON THIS ENGINE BUILD.
    //
    // Godot 4.7-dev4 carries upstream 110287, which makes ShaderMaterial's property-list pass push a
    // default at the material for every uniform it has no cached value for (an unassigned sampler2D
    // on any of our UI or text shaders). Duplicate() runs that pass on a material whose RID is not
    // there to receive it, and the server prints "Parameter material is null" once per uniform, per
    // duplicate, per apply: a thousand lines a minute with the dash open. Upstream reverted it in
    // 118373 for dev5. A clone built by hand never enters that pass: same shader, the uniforms that
    // were set copied across, the unset ones left to the shader's own defaults exactly as before.
    // Everything else still duplicates; nothing in BaseMaterial3D takes that path. -xlinka
    protected static Material CloneMaterial(Material source)
    {
        if (source is not ShaderMaterial shaderMaterial)
            return (Material)source.Duplicate();

        var clone = new ShaderMaterial
        {
            Shader = shaderMaterial.Shader,
            RenderPriority = shaderMaterial.RenderPriority,
            NextPass = shaderMaterial.NextPass,
        };
        var shader = shaderMaterial.Shader;
        if (shader == null)
            return clone;

        foreach (var entry in shader.GetShaderUniformList())
        {
            var info = entry.AsGodotDictionary();
            if (!info.TryGetValue("name", out var nameVariant))
                continue;
            var name = nameVariant.AsStringName();
            var value = shaderMaterial.GetShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil)
                clone.SetShaderParameter(name, value);
        }
        return clone;
    }

    private readonly LoadingSkinWatch _loadingSkin = new();

    // The asset hook whose ArrayMesh this renderer is drawing, when it is one. It knows which submesh each
    // surface came from. A procedural mesh gets no answer: its generators number their materials by surface
    // already (a canvas chunk counts only the submeshes that draw), and mapping them would shift every one.
    private MeshAssetHook? _surfaceSource;

    private Material GetSurfaceMaterial(int surface)
    {
        // Materials are numbered by submesh and an empty submesh makes no surface: read the material of the
        // submesh this surface came from, or everything after an empty one wears its neighbour's. -xlinka
        int index = _surfaceSource?.SubmeshForSurface(surface) ?? surface;

        // Assigned but still arriving: wear the loading skin rather than whatever half-state the
        // material is in (a white untextured PBS, or nothing at all). The swap back to the real
        // material rides the asset-arrival notification that already re-drives this hook, and the
        // core-side latch makes sure it only happens once. -xlinka
        if (Owner.IsSurfaceLoading(index))
        {
            _loadingSkin.Note(index, Owner.SurfaceMaterialProvider(index), Owner.Slot?.SlotName.Value);
            _surfaceStillLoading = true;
            return LoadingPlaceholderMaterial.Get();
        }

        _loadingSkin.Clear(index, Owner.Slot?.SlotName.Value);

        var materialAsset = GetMaterialAsset(index);
        var provider = Owner.SurfaceMaterialProvider(index);
        if (materialAsset?.GodotMaterial == null && provider != null
            && (provider is not MaterialProvider materialProvider || materialProvider.UseLoadingPlaceholder))
        {
            _surfaceStillLoading = true;
            return LoadingPlaceholderMaterial.Get();
        }
        if (materialAsset == null)
        {
            return null!;
        }

        var propertyBlock = GetPropertyBlockAsset(index);
        if (propertyBlock != null && propertyBlock.IsValid)
        {
            return ApplySurfaceRenderPriority(index, (propertyBlock.ApplyToMaterial(materialAsset) as Material)!);
        }

        // A surface with a priority of its own writes it onto the material it wears, so it keeps the
        // component's own; every other surface wears the copy identical materials share.
        if (Owner.GetSurfaceRenderPriority(index) != Lumora.Core.Components.MeshRenderer.NoSurfaceRenderPriority)
            return ApplySurfaceRenderPriority(index, (materialAsset.GodotMaterial as Material)!);
        return (materialAsset.SharedGodotMaterial as Material)!;
    }

    private Material ApplySurfaceRenderPriority(int index, Material material)
    {
        if (material == null)
        {
            return null!;
        }

        int priority = Owner.GetSurfaceRenderPriority(index);
        if (priority == Lumora.Core.Components.MeshRenderer.NoSurfaceRenderPriority)
        {
            return material;
        }

        if (material.RenderPriority == priority)
        {
            return material;
        }

        MaterialPropertyApplicator.ApplyGodotRenderPriority(material, priority);
        return material;
    }

    // Roughly ten seconds at 60 Hz. A local decode lands in a handful of frames; a peer transfer can
    // take longer, and waiting is free compared with the object never appearing.
    private const int MeshWaitFrameLimit = 600;

    private const int MaxPushAttempts = 3;
    private int _pushAttempts;

    // Holds one MeshProvider loaded on this renderer's behalf. Swapped when the renderer is pointed at
    // a different mesh, dropped when the hook goes away.
    private sealed class ProviderPin : Lumora.Core.Assets.IAssetRef
    {
        private Lumora.Core.Assets.IAssetProvider _target = null!;

        public Lumora.Core.Assets.IAssetProvider Target
        {
            get => _target;
            set => _target = value;
        }

        // The renderer re-reads the provider every apply and is re-driven by its own requeue, so there
        // is nothing to do on the notification itself.
        public void AssetUpdated() { }
    }

    private ProviderPin? _providerPin;
    private MeshProvider? _pinnedProvider;

    private void PinProvider(MeshProvider provider)
    {
        if (ReferenceEquals(_pinnedProvider, provider))
            return;

        ReleaseProviderPin();

        _providerPin = new ProviderPin { Target = provider };
        _pinnedProvider = provider;
        provider.ReferenceSet(_providerPin);
    }

    private void ReleaseProviderPin()
    {
        if (_providerPin != null && _pinnedProvider is { IsDestroyed: false })
            _pinnedProvider.ReferenceFreed(_providerPin);
        _providerPin = null;
        _pinnedProvider = null;
    }

    // Two seconds at 60 Hz: long past any reference-resolution pass, short enough to be in the log
    // before anyone goes looking.
    private const int MissingGraceApplies = 120;
    private int _meshWaitFrames;
    private bool _meshWaitGaveUp;

    // ONE LINE PER UNSKINNED RENDERER, once, the first time it has a mesh.
    //
    // The skinned hook has had this for a while and it is how every skinning problem since has been
    // found; the plain one had nothing, so a mesh that simply did not appear left no trace at all and
    // the only honest answer to "where is it" was a shrug. Everything here is something that makes a
    // renderer invisible while every field you can reach from the datamodel still reads correct:
    // hidden, no surfaces, an empty or holed material list, bounds the culler throws away, a node
    // parked somewhere other than where the slot is, a degenerate or enormous scale, or a layer the
    // camera does not draw. -xlinka
    private bool _reported;

    private void ReportOnce()
    {
        if (_reported || !LumoraLogger.EnableDebug)
            return;

        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance) || meshInstance.Mesh == null)
            return;

        _reported = true;

        // A canvas chunk is the engine's own and spends its life looking like every case below: empty and
        // switched off while it fills, a material list with spare entries. Every card of a graph printed
        // one, a few hundred lines a world. -xlinka
        if (Owner.Mesh.Target is Helio.UI.LocalMeshSource)
            return;

        var aabb = meshInstance.GetAabb();
        int surfaces = meshInstance.Mesh.GetSurfaceCount();

        int filled = 0;
        for (int i = 0; i < Owner.Materials.Count; i++)
        {
            if (Owner.Materials.GetElement(i)?.Target != null)
                filled++;
        }

        string report = ($"MeshRendererHook '{Owner.Slot?.SlotName.Value}': {surfaces} surface(s) | "
                       + $"visible={meshInstance.Visible} enabled={Owner.Enabled} "
                       + $"materials={filled}/{Owner.Materials.Count} "
                       + $"aabb=({aabb.Size.X:F2},{aabb.Size.Y:F2},{aabb.Size.Z:F2}) "
                       + $"at ({aabb.Position.X:F2},{aabb.Position.Y:F2},{aabb.Position.Z:F2}) | "
                       + $"node=({meshInstance.GlobalPosition.X:F2},{meshInstance.GlobalPosition.Y:F2},{meshInstance.GlobalPosition.Z:F2}) "
                       + $"scale=({meshInstance.Scale.X:F3},{meshInstance.Scale.Y:F3},{meshInstance.Scale.Z:F3}) "
                       + $"layers={meshInstance.Layers} "
                       // The slot's own global, so a node sitting somewhere the slot is not shows up as
                       // the two disagreeing rather than as one suspicious number.
                       + $"slot=({Owner.Slot?.GlobalPosition.x ?? 0f:F2},{Owner.Slot?.GlobalPosition.y ?? 0f:F2},{Owner.Slot?.GlobalPosition.z ?? 0f:F2}) "
                       + $"slotActive={Owner.Slot?.IsActive}");
        LumoraLogger.Debug(report);
    }

    // At least one slot in the list actually has a material in it.
    // A renderer that has never yet been drawn dressed stays out of sight while its materials still wait for their
    // pictures, for up to DressWaitSeconds, so an object arrives with its mesh, material and pictures together
    // instead of as the loading skin first and its pictures a moment later. A local load is done well inside the
    // wait; a slower one (a picture coming from another machine) shows the loading skin when the wait runs out.
    // Only the first time: a material swapped later never hides what is already on screen. -xlinka
    // A cold world import wires its pictures last and has them on the GPU up to about 3.5 s after its meshes.
    private const double DressWaitSeconds = 4.0;
    private long _dressWaitFrom;
    private bool _dressed;
    private bool _materialsApplied;

    private void ApplyVisible()
    {
        bool enabled = Owner.Enabled && HasAnyMaterial() && !Owner.IsLocallyHidden;
        // Not for a panel: its chunks show at once, and one waiting on a picture from the web keeps its loading skin.
        if (enabled && !_dressed && meshInstance != null && Owner.Mesh.Target is Helio.UI.LocalMeshSource)
            _dressed = true;
        if (enabled && !_dressed && meshInstance != null)
        {
            // The wait counts from when there is geometry to show, and nothing is shown before the first material
            // pass has said whether anything is still arriving.
            if (meshInstance.Mesh is not { } shown || shown.GetSurfaceCount() == 0)
                enabled = false;
            else
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_dressWaitFrom == 0)
                    _dressWaitFrom = now;
                if ((_materialsApplied && !_surfaceStillLoading) || now - _dressWaitFrom > DressWaitSeconds * System.Diagnostics.Stopwatch.Frequency)
                    _dressed = true;
                else
                    enabled = false;
            }
        }
        if (meshInstance != null && meshInstance.Visible != enabled)
            meshInstance.Visible = enabled;
    }

    private bool HasAnyMaterial()
    {
        for (int i = 0; i < Owner.Materials.Count; i++)
        {
            if (Owner.Materials.GetElement(i) is { } reference
                && (reference.Target != null || !reference.Value.IsNull))
                return true;
        }
        return false;
    }

    private MaterialAsset GetMaterialAsset(int index)
    {
        if (Owner.Materials.Count == 0)
        {
            return null!;
        }

        int materialIndex = System.Math.Min(index, Owner.Materials.Count - 1);
        return Owner.Materials.GetElement(materialIndex).Asset;
    }

    private MaterialPropertyBlockAsset GetPropertyBlockAsset(int index)
    {
        if (Owner.MaterialPropertyBlocks.Count == 0 || index >= Owner.MaterialPropertyBlocks.Count)
        {
            return null!;
        }

        return Owner.MaterialPropertyBlocks.GetElement(index).Asset;
    }

    private int GetSurfaceCount()
    {
        return meshInstance?.Mesh?.GetSurfaceCount() ?? 0;
    }

    private static bool AnyChanged<A>(SyncAssetList<A> list) where A : Asset
    {
        bool changed = false;
        foreach (var assetRef in list)
        {
            changed |= assetRef.GetWasChangedAndClear();
        }

        return changed;
    }

    private static bool AnyChanged(SyncIntList list)
    {
        bool changed = false;
        foreach (var field in list)
        {
            changed |= field.GetWasChangedAndClear();
        }

        return changed;
    }

    private void CleanupRenderer(bool destroyingWorld)
    {
        WatchSlot(false);
        // Per-surface instances sit beside the instance under the slot's node, so they go one by one. -xlinka
        if (!destroyingWorld)
        {
            foreach (var surface in _perSurfaceInstances)
            {
                if (GodotObject.IsInstanceValid(surface))
                    surface.QueueFree();
            }
        }
        _perSurfaceInstances.Clear();
        _appliedMaterials.Clear();
        _rangeApplied = false;
        _hasPlaced = false;
        _occludable = true;
        DirectPoses.InstancesFreed++;
        if (!destroyingWorld && MeshRenderer != null && GodotObject.IsInstanceValid(MeshRenderer))
        {
            ((Node)MeshRenderer).QueueFree();
        }
        // A renderer that comes back (its mesh set again) builds a fresh instance rather than reaching for the freed one.
        meshInstance = null!;
        MeshRenderer = default!;
        OnCleanupRenderer();
    }

    // A procedural mesh can hand out a different Godot mesh than the one this renderer holds: identical
    // meshes share one, and a mesh whose data moves on leaves the shared one for its own. The mesh's hook
    // says so and the renderer picks the new one up on its next apply. -xlinka
    private MeshHook? _meshSource;
    private bool _meshSourceReplaced;

    private void WatchMeshSource(MeshHook? source)
    {
        if (ReferenceEquals(_meshSource, source))
            return;
        if (_meshSource != null)
        {
            _meshSource.MeshReplaced -= OnMeshSourceReplaced;
            _meshSource.SurfacesRebuilt -= OnMeshSourceRebuilt;
        }
        _meshSource = source;
        if (source != null)
        {
            source.MeshReplaced += OnMeshSourceReplaced;
            source.SurfacesRebuilt += OnMeshSourceRebuilt;
        }
    }

    private void OnMeshSourceReplaced()
    {
        _meshSourceReplaced = true;
        if (Owner is { IsDestroyed: false })
            Owner.MarkChangeDirty();
    }

    // What ApplyMaterials last put on each surface of the instance, overlay copies included.
    private readonly System.Collections.Generic.List<Material?> _appliedMaterials = new();

    // The mesh this renderer draws was just emptied and filled again in place, which took the material off every
    // surface after the first (MeshHook.SurfacesRebuilt). They go straight back on, so the frame about to be drawn
    // never has a surface in the engine's plain default; the queued apply the rebuild also asked for still runs for
    // anything else it changed. -xlinka
    private void OnMeshSourceRebuilt()
    {
        var instance = meshInstance;
        if (instance == null || !GodotObject.IsInstanceValid(instance) || instance.Mesh is not { } shown
            || !ReferenceEquals(shown, _meshSource?.GodotMesh))
            return;
        int count = System.Math.Min(shown.GetSurfaceCount(), _appliedMaterials.Count);
        bool restored = false;
        for (int i = 0; i < count; i++)
        {
            var material = _appliedMaterials[i];
            if (material == null || ReferenceEquals(instance.GetSurfaceOverrideMaterial(i), material))
                continue;
            instance.SetSurfaceOverrideMaterial(i, material);
            restored = true;
        }
        // an instance value is filed against the material it wears
        if (restored)
        {
            _instanceParamsStale = true;
            ApplyInstanceParams();
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        BatchedBy?.RendererChanged();
        BatchedBy = null;
        WatchMeshSource(null);
        // A world going down is tearing everything out anyway; touching providers on the way past
        // would only be a chance to trip over something already gone.
        if (!destroyingWorld)
            ReleaseProviderPin();
        CleanupRenderer(destroyingWorld);
        meshInstance = null!;
        MeshRenderer = default!;
        base.Destroy(destroyingWorld);
    }

    protected virtual bool IsMeshAssetAvailable()
    {
        return Owner.Mesh.Target != null;
    }

    // ProceduralMesh.OverrideBoundingBox was a field nothing read. It matters for any mesh whose
    // shader moves vertices far from the authored geometry: Godot culls on the mesh AABB, so the
    // bubble field vanished the moment its origin left the frustum. A zero Aabb clears the override. -xlinka
    private void ApplyBoundsOverride()
    {
        if (meshInstance == null) return;

        var aabb = new Aabb();
        if (Owner.Mesh.Target is LumoraMeshes.ProceduralMesh proceduralMesh && proceduralMesh.OverrideBoundingBox.Value)
        {
            var box = proceduralMesh.OverridenBoundingBox.Value;
            var min = box.Min;
            var size = box.Max - box.Min;
            aabb = new Aabb(new Vector3(min.x, min.y, min.z), new Vector3(size.x, size.y, size.z));
        }

        _customBounds = aabb;
        if (meshInstance.CustomAabb != aabb)
            meshInstance.CustomAabb = aabb;
    }

    protected virtual Mesh GetGodotMeshFromAsset()
    {
        var meshComponent = Owner.Mesh.Target;
        if (meshComponent == null) return null!;

        if (meshComponent is LumoraMeshes.ProceduralMesh proceduralMesh)
        {
            if (proceduralMesh.Hook is MeshHook meshHook)
            {
                WatchMeshSource(meshHook);
                if (meshHook.GodotMesh != null)
                    return meshHook.GodotMesh;
            }
            LumoraLogger.Debug("MeshRendererHookBase: ProceduralMesh hook not ready");
            return null!;
        }

        // Imported / asset-system meshes: a MeshProvider supplies a MeshDataAsset whose hook (MeshAssetHook) has
        // already built the Godot ArrayMesh from the decoded PhosMesh. Hand that straight to the renderer. Without
        // this, MeshProvider meshes never rendered and spammed "Unsupported mesh component type MeshProvider". -xlinka
        if (meshComponent is MeshProvider meshProvider)
        {
            // NOBODY WAS EVER ASKING THIS PROVIDER TO LOAD.
            //
            // StaticAssetProvider.OnUrlChanged only starts a load `if (AssetReferenceCount > 0)`, and
            // the count comes from AssetRef members registering themselves. SkinnedMeshRenderer holds
            // its geometry through `AssetRef<MeshDataAsset> MeshAsset`, so it refcounts and loads;
            // MeshRenderer holds a plain `SyncRef<Component> Mesh`, which refcounts nothing. So an
            // unskinned renderer pointed at a MeshProvider sat forever in front of an asset that had
            // never been asked to exist - which is why every prop on an imported avatar was invisible
            // while everything skinned came through.
            //
            // Taking the reference here rather than adding an AssetRef member to MeshRenderer, because
            // a new sync member would change the component's saved shape for every world already on
            // disk. This pin costs one object per renderer and is released with the hook. -xlinka
            PinProvider(meshProvider);

            if (meshProvider.Asset?.Hook is MeshAssetHook meshAssetHook)
            {
                // THE UPLOAD IS FIRE-AND-FORGET AND CAN MISS. MeshDataAsset.SetMeshData pushes its
                // geometry into the hook only `if (Hook != null)` at that instant, never retries, and
                // bumps a Version nobody is watching. An asset that decoded before its hook was ready
                // therefore holds perfectly good geometry that the hook has never seen - and since the
                // hook's ArrayMesh is the ONLY path an unskinned renderer has, the object never
                // appears. Skinned meshes are immune because they read the PhosMesh themselves.
                //
                // That is the whole reason an imported avatar showed its body and none of its props.
                // Push it now rather than waiting for a second upload that is not coming. -xlinka
                // A few attempts, not six hundred: the upload defers to the main thread, so re-issuing
                // it every frame of the wait would queue the same mesh over and over for a hook that
                // has already been told.
                if (!meshAssetHook.IsValid && meshProvider.Asset.MeshData != null && _pushAttempts < MaxPushAttempts)
                {
                    _pushAttempts++;
                    meshAssetHook.UploadMesh(meshProvider.Asset.MeshData);
                }

                if (meshAssetHook.IsValid)
                {
                    _surfaceSource = meshAssetHook;
                    return meshAssetHook.GodotMesh;
                }
            }
            // Asset still decoding/uploading (the ArrayMesh builds deferred on the main thread) - not an error;
            // the renderer re-applies when the asset finishes.
            LumoraLogger.Debug("MeshRendererHookBase: MeshProvider mesh asset/hook not ready yet");
            return null!;
        }

        LumoraLogger.Warn($"MeshRendererHookBase: Unsupported mesh component type {meshComponent.GetType().Name}");
        return null!;
    }

    protected virtual void ApplySortingOrder(int sortingOrder)
    {
        ApplyRenderQueue();
    }

    protected virtual void ApplyRenderQueue()
    {
        if (meshInstance == null)
        {
            return;
        }

        if (Owner.HasOverlayOrder)
        {
            meshInstance.SortingOffset = Owner.OverlayOrder;
            return;
        }

        // Owner.Material ADDS an entry when the list is empty: read the list instead. Asked of a renderer whose
        // materials had not been linked yet, it put an empty material first, and every material after it
        // landed one surface late (the first surface drew Godot's plain white). -xlinka
        var materialQueue = Owner.Materials.Count > 0 ? Owner.Materials.GetElement(0).Asset?.RenderQueue ?? -1 : -1;
        var effectiveQueue = (materialQueue >= 0 ? materialQueue : 0) + Owner.SortingOrder.Value;

        // Godot uses SortingOffset for transparent object ordering, while Material.RenderPriority
        // applies per-material. Keep them in sync so Lumora's material RenderQueue affects the
        // renderer as well as the material resource.
        meshInstance.SortingOffset = effectiveQueue;
    }

    protected virtual void ApplyShadowCastMode(ShadowCastMode shadowCastMode)
    {
        if (meshInstance != null)
        {
            switch (shadowCastMode)
            {
                case ShadowCastMode.Off:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                    break;
                case ShadowCastMode.On:
                    // On casts with the material's own culling, so a material with Culling None already casts
                    // from both faces and a quad keeps its shadow with the light behind it, on both renderers.
                    // DoubleSided is only for a back-culled surface that should cast from its back as well.
                    // -xlinka
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
                    break;
                case ShadowCastMode.ShadowOnly:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
                    break;
                case ShadowCastMode.DoubleSided:
                    meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided;
                    break;
            }
        }
    }

}
