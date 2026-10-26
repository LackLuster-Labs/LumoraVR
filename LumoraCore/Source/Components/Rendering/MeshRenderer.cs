// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;

namespace Lumora.Core.Components;

[ComponentCategory("Rendering")]
public class MeshRenderer : ImplementableComponent
{
    public const int NoSurfaceRenderPriority = int.MinValue;

    public readonly SyncRef<Component> Mesh;

    public readonly SyncAssetList<MaterialAsset> Materials;
    public readonly SyncAssetList<MaterialPropertyBlockAsset> MaterialPropertyBlocks;
    public readonly SyncIntList SurfaceRenderPriorities;

    public readonly Sync<ShadowCastMode> ShadowCastMode;

    // lower values render first
    public readonly Sync<int> SortingOrder;

    // When true, the hook renders each mesh surface as its OWN MeshInstance3D ordered purely by a distinct
    // per-surface SortingOffset (= SortingOrder * a stride + surface index), with uniform render_priority. This
    // bypasses Godot's 256-level render_priority cap to give unbounded positional order for UI. Set by Helio's
    // GraphicsChunk when the unbounded-ordering mode is enabled; off for all normal (world) meshes. -xlinka
    public bool PerSurfaceOrdering { get; set; }

    // The stride between two renderers' keys in that mode: a renderer's surfaces take SortingOrder * stride + index.
    public const float PerSurfaceSortStride = 1024f;

    // In that mode, a key for surface 0 in place of its own, so it can draw ahead of renderers earlier in order:
    // a window's backing goes down before anything of any window. Null keeps the usual key.
    public float? LeadSurfaceSortingOffset { get; set; }

    // Extra frustum-cull margin the hook applies to the mesh instance(s), in the MESH'S OWN LOCAL UNITS -
    // the renderer grows the local bounding box by this and applies the node transform afterwards, so on a
    // slot scaled to 0.001 a margin of 1 buys one millimetre of world, not one metre. Getting that backwards
    // is how scrolled UI ended up with a four-millimetre margin against metres of displacement. Meshes whose
    // vertices move in the VERTEX SHADER need it: the instance AABB is computed from the baked, undisplaced
    // verts, so without a margin Godot culls the instance while the displaced pixels are still on screen.
    // Set by Helio's canvas per chunk (see Canvas.ChunkCullMargin); zero for normal meshes. -xlinka
    public float ExtraCullMargin { get; set; }

    // Distance in metres past which the hook stops drawing this renderer, 0 = draws at any distance, with
    // ViewDistanceFadeMargin as the dissolve band in front of the cut. Pushed in by whatever owns the
    // renderer rather than authored on it: a Canvas culling its chunk meshes, a TextRenderer culling the
    // child renderer it built for a sign. The hook folds it into the same Godot visibility range a LodGroup
    // uses and keeps the tighter of the two, so the two systems can't undo each other. -xlinka
    public float MaxViewDistance { get; set; }
    public float ViewDistanceFadeMargin { get; set; }

    // The render layers this renderer draws on in place of the ones it would inherit, 0 for none. Set by a
    // view material override, which shows a renderer one way to some views and another way to the rest; a
    // plain property, never replicated, since every peer works out its own views. -xlinka
    public int ViewLayers { get; private set; }

    public void SetViewLayers(int layers)
    {
        if (ViewLayers == layers)
            return;
        ViewLayers = layers;
        RunApplyChanges();
    }

    // Draw-over-everything band for this viewer's own hands and lasers while the dash is up. When set,
    // the hook writes this ABSOLUTE sorting offset on the instance in place of RenderQueue + SortingOrder
    // and duplicates every surface material into the transparent pass at the top render priority, because
    // an opaque-pass mesh is drawn before every transparent surface no matter what offset it carries and
    // the dash is a transparent-pass surface. OverlayDepthTest keeps the depth test on the duplicate: the
    // dash and the other overlay shaders write no depth, so a depth-tested hand drawn after them is never
    // cut by them, and it keeps occluding itself; turning the test off also drops the depth write, which
    // makes a skeleton hand draw its bones through its joints. Plain properties, never replicated: what
    // draws over YOUR dash is a per-viewer matter. Clearing puts the asset materials and the normal offset
    // back untouched. -xlinka
    public const int NoOverlayOrder = int.MinValue;
    public int OverlayOrder { get; private set; } = NoOverlayOrder;
    public bool OverlayDepthTest { get; private set; } = true;
    public bool OverlayOrderChanged { get; set; }
    public bool HasOverlayOrder => OverlayOrder != NoOverlayOrder;

    public void SetOverlayOrder(int order, bool depthTest = true)
    {
        if (OverlayOrder == order && OverlayDepthTest == depthTest)
            return;
        OverlayOrder = order;
        OverlayDepthTest = depthTest;
        OverlayOrderChanged = true;
        RunApplyChanges();
    }

    public void ClearOverlayOrder()
    {
        if (OverlayOrder == NoOverlayOrder)
            return;
        OverlayOrder = NoOverlayOrder;
        OverlayDepthTest = true;
        OverlayOrderChanged = true;
        RunApplyChanges();
    }

    // A mesh this renderer does not draw while its Mesh points at it, on this machine only. A SquishyBody
    // sets it on renderers that draw the mesh it deforms, since it draws the deformed copy itself. Kept as
    // the mesh rather than a bool so retargeting the renderer brings it back without anyone clearing it.
    // Never replicated: every peer runs its own soft body. -xlinka
    private Component? _locallyHiddenMesh;

    public bool IsLocallyHidden => _locallyHiddenMesh != null && ReferenceEquals(Mesh.Target, _locallyHiddenMesh);

    public void SetLocallyHiddenMesh(Component? mesh)
    {
        if (ReferenceEquals(_locallyHiddenMesh, mesh))
            return;
        _locallyHiddenMesh = mesh;
        RunApplyChanges();
    }

    // Four numbers this instance hands its shader, on this machine only, so a crowd of renderers sharing one
    // material still draw each their own way without a copy of the material each. The shader reads them as
    // `instance uniform vec4 instance_params`; one that declares no such uniform never sees them. Never
    // replicated: these carry a viewer's own state. -xlinka
    public float4 InstanceParams { get; private set; }
    public bool HasInstanceParams { get; private set; }
    public bool InstanceParamsChanged { get; set; }

    public void SetInstanceParams(in float4 value)
    {
        if (HasInstanceParams && InstanceParams == value)
            return;
        InstanceParams = value;
        HasInstanceParams = true;
        InstanceParamsChanged = true;
        RunApplyChanges();
    }

    public bool MaterialsChanged { get; set; }
    public bool MaterialPropertyBlocksChanged { get; set; }
    public bool SurfaceRenderPrioritiesChanged { get; set; }
    public bool SurfaceAssignmentsChanged { get; set; }

    // Mesh DATA changing (ClearSurfaces + re-add) is invisible to the SyncRef WasChanged path,
    // so callers that rebuild the underlying ArrayMesh contents in place must flag us dirty
    // explicitly or surface override materials end up stale. - xlinka
    public void FlagSurfacesDirty()
    {
        SurfaceAssignmentsChanged = true;
        RunApplyChanges();
    }

    public void EnsureExactSurfaceRenderPriorityCount(int count)
    {
        if (SurfaceRenderPriorities.EnsureExactCount(count, NoSurfaceRenderPriority))
        {
            SurfaceRenderPrioritiesChanged = true;
        }
    }

    public void SetSurfaceRenderPriority(int index, int priority)
    {
        if (SurfaceRenderPriorities.GetValue(index, NoSurfaceRenderPriority) == priority)
        {
            return;
        }

        SurfaceRenderPriorities.SetValue(index, priority);
        SurfaceRenderPrioritiesChanged = true;
    }

    public int GetSurfaceRenderPriority(int index)
    {
        return SurfaceRenderPriorities.GetValue(index, NoSurfaceRenderPriority);
    }

    public AssetRef<MaterialAsset> Material
    {
        get
        {
            if (Materials.Count == 0)
            {
                return Materials.Add();
            }

            return Materials.GetElement(0);
        }
    }

    private readonly LoadingSurfaceLatch _loadingSurfaces = new();

    // True while this surface's material is a thing that IS coming but has not arrived - so the hook
    // paints the loading skin over it instead of whatever half-state the material is in. A surface with
    // NO material assigned is authored, not loading, and answers false: an untextured mesh is somebody's
    // intent, not a gap. Purely event-driven - the arrival notification that lands the texture already
    // re-drives the renderer, so nothing here runs per frame. -xlinka
    public bool IsSurfaceLoading(int surfaceIndex)
    {
        int index = SurfaceMaterialIndex(surfaceIndex);
        if (index < 0)
            return false;
        var reference = Materials.GetElement(index);
        return (!reference.Value.IsNull && reference.Target == null)
            || _loadingSurfaces.IsLoading(index, reference.Target);
    }

    // The material that governs a given surface. Same clamp IsSurfaceLoading applies, so a diagnostic
    // that asks what a surface is waiting on names the material that surface actually gets.
    public IAssetProvider<MaterialAsset>? SurfaceMaterialProvider(int surfaceIndex)
    {
        int index = SurfaceMaterialIndex(surfaceIndex);
        return index < 0 ? null : Materials.GetElement(index).Target;
    }

    // Surfaces past the end of the list share the last material, which is what the hook paints.
    private int SurfaceMaterialIndex(int surfaceIndex)
    {
        int count = Materials.Count;
        if (count == 0 || surfaceIndex < 0)
            return -1;
        return surfaceIndex < count ? surfaceIndex : count - 1;
    }

    public MeshRenderer()
    {
        Mesh = new SyncRef<Component>(this);
        Materials = new SyncAssetList<MaterialAsset>(this);
        MaterialPropertyBlocks = new SyncAssetList<MaterialPropertyBlockAsset>(this);
        SurfaceRenderPriorities = new SyncIntList(this);
        ShadowCastMode = new Sync<ShadowCastMode>(this, Components.ShadowCastMode.On);
        SortingOrder = new Sync<int>(this, 0);
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Materials.OnChanged += OnMaterialsChanged;
        MaterialPropertyBlocks.OnChanged += OnMaterialPropertyBlocksChanged;
        SurfaceRenderPriorities.OnChanged += OnSurfaceRenderPrioritiesChanged;
        // The Mesh ref is a plain SyncRef<Component>, which (unlike AssetRef) does NOT re-fire us when its
        // target resolves. On a joiner the mesh provider can decode AFTER us, so our first apply has no
        // geometry to read. Re-pull when the provider resolves or the ref retargets, so we don't stay
        // invisible. (The provider also pokes us via FlagSurfacesDirty once it uploads geometry.) -xlinka
        Mesh.OnObjectAvailable += OnMeshRefResolved;
        Mesh.OnTargetChange += OnMeshRefResolved;
    }

    private void OnMeshRefResolved(SyncRef<Component> reference)
    {
        FlagSurfacesDirty();
    }

    // A list element's own change never reaches this component (SyncList does not forward element
    // events), so a material that finishes loading inside the list only ever arrives through this
    // callback. Raising the flag alone left the surface on its loading placeholder until something
    // unrelated dirtied the renderer; the apply has to be queued from here, like every other list. -xlinka
    private void OnMaterialsChanged(SyncAssetList<MaterialAsset> list)
    {
        MaterialsChanged = true;
        RunApplyChanges();
    }

    private void OnMaterialPropertyBlocksChanged(SyncAssetList<MaterialPropertyBlockAsset> list)
    {
        MaterialPropertyBlocksChanged = true;
        RunApplyChanges();
    }

    private void OnSurfaceRenderPrioritiesChanged(SyncIntList list)
    {
        SurfaceRenderPrioritiesChanged = true;
        RunApplyChanges();
    }

    public override void OnDestroy()
    {
        Materials.OnChanged -= OnMaterialsChanged;
        MaterialPropertyBlocks.OnChanged -= OnMaterialPropertyBlocksChanged;
        SurfaceRenderPriorities.OnChanged -= OnSurfaceRenderPrioritiesChanged;
        base.OnDestroy();
    }

    // A solid collider shaped as the convex hull of what this draws, on its slot, so the object can be stood
    // on, thrown or carried by a rigid body. One already there for the same mesh is left as it is.
    [SyncMethod]
    public void AddConvexHullCollider()
    {
        if (Lumora.Core.Components.Meshes.ConvexHullPointSource.Resolve(this) is { } mesh)
            ConvexHullCollider.Ensure(Slot, mesh);
    }

    // Solid colliders for what this draws, as several convex hulls worked out in a dialog, for a shape one hull
    // would fill in.
    [SyncMethod]
    public void SplitIntoConvexHulls()
    {
        if (Lumora.Core.Components.Meshes.ConvexHullPointSource.Resolve(this) != null)
            ConvexSplitDialog.Open(this);
    }
}
