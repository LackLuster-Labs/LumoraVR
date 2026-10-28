
// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Helio.UI.Layout;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Assets;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Components.Touch;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Helio.UI;

// root of a UI tree. attach to a slot to make it (and descendants) a UI subtree. - xlinka
[ComponentCategory("UI/Helio")]
public class Canvas : Component, ILaserPointerTarget, ILaserAxisTarget, ILaserSecondaryTarget, ITouchSurface
{
    private sealed class PointerState
    {
        public IUIInteractable? Hovered;
        public IUIInteractable? Pressed;
        public UIInteractionContext LastContext;
        public bool IsPressed;
        // Drag arbitration: where the press landed (canvas-local) and whether we've already handed the
        // press off to a scrolling ancestor this gesture.
        public float2 PressPoint;
        public bool Transferred;
        // The press was handed on to what sat under the pointer after the element first pressed stopped
        // taking input. It was never a click on what it landed on, so letting go does not submit it.
        public bool HandedOff;
        // A laser press a finger took over: held down until the trigger comes up, so the same squeeze
        // cannot re-press once the finger leaves.
        public bool PressSuppressed;
        // The trigger eased off its deep level and let go of a click, but is still held: the next deep
        // squeeze presses again (see StepDeepTrigger).
        public bool DeepRearmed;
        // The world update a deep edge was last taken on, so a second pass in the same update cannot
        // take it twice.
        public ulong DeepUpdate = ulong.MaxValue;
        // The hover areas this pointer is over, whatever under them took it.
        public List<HoverArea>? Areas;
    }

    private readonly Dictionary<int, PointerState> _pointers = new();
    // Filled by the scan of a pointer update, null for every other scan.
    private List<HoverArea>? _areaScan;
    private readonly List<HoverArea> _areaScratch = new();
    private RectTransform? _rootRect;
    private GraphicChunkRoot? _chunkRoot;
    private GraphicsChunk? _rootChunk;
    // Optional physical collider sized to the aggregated UI bounds (grab/physics raycast against the canvas
    // surface). Attached only when SizeCollider is on; default off = purely additive. -xlinka
    private BoxCollider? _uiCollider;

    // Canvas-wide sorting boost fed into every chunk renderer's SortingOffset.
    // High values make the whole canvas draw in front of other transparent
    // geometry - overlay UI like the context menu uses this to render on top
    // of everything.
    public readonly Sync<int> SortingOrder = new();

    // Overlay canvas: every material this canvas mints gets ZTest Always, so the whole canvas skips the
    // depth test and can never be occluded or cut into by world geometry. SortingOrder alone only wins the
    // transparent SORT - the depth test still clips UI pixels against opaque world geometry, which is how
    // world objects "show through" a sorted-on-top menu. Set before the first build (mint-time flag). -xlinka
    public readonly Sync<bool> Overlay = new();

    // Whose canvas this is. Set, it answers that user's lasers and fingers alone: everyone else sees it and
    // points straight through it, so nothing on it hovers, presses or scrolls for them. An Overlay canvas
    // with an owner draws over the world on the owner's machine only; anyone else sees it stand in the world,
    // depth tested, rather than through walls and through the owner's own head. Null lets anyone in. Set
    // before the first build, like Overlay. -xlinka
    public readonly SyncRef<User> Exclusive = new();

    // Whether a pointer this user drives may reach the canvas. No user means this machine's own.
    public bool AcceptsPointerFrom(User? actor)
    {
        if (Exclusive.Value.IsNull)
            return true;
        var owner = Exclusive.Target;
        return owner != null && ReferenceEquals(owner, actor ?? World?.LocalUser);
    }

    // Overlay as this machine draws it.
    public bool DrawsOverlay => Overlay.Value && AcceptsPointerFrom(null);

    // Which faces of the canvas's pictures draw, counted from the side it is read from: Back (the default)
    // draws the reading side only, None both, Front only the side behind. A graphic wearing a material of
    // its own keeps that material's culling, and text draws the reading side only. -xlinka
    public readonly Sync<Culling> Culling = new(Lumora.Core.Assets.Culling.Back);

    // Read from the slot's backward (-Z) side instead of its forward (+Z) one: the canvas turns half a turn
    // about its up axis, pictures, colliders and pointers alike. A canvas brought in from a package reads
    // this way. -xlinka
    public readonly Sync<bool> FaceBackward = new();

    // A small distance-field expansion keeps fine strokes readable at panel scale without widening
    // the shader's screen-space antialiasing edge. Zero remains available for the original face. -xlinka
    public readonly Sync<colorHDR> TextOutlineColor = new();
    public readonly Sync<float> TextOutlineThickness = new();
    public readonly Sync<float> TextFaceDilate = new(0.035f);
    public readonly Sync<float> TextFaceSoftness = new();
    public readonly Sync<colorHDR> TextUnderlayColor = new();
    public readonly Sync<float2> TextUnderlayOffset = new();
    public readonly Sync<float> TextUnderlaySoftness = new();

    // Opt-in: size a BoxCollider on the canvas slot to the aggregated UI bounds, for physical/grab
    // interaction against the surface. Off by default - leaves hit-testing on the existing raycast path. -xlinka
    public readonly Sync<bool> SizeCollider = new();

    // Width and height in canvas units, centred on the slot. An axis of zero leaves the root rect as it is
    // there; one above zero sizes the root rect, and the box collider on the slot, to it. For a canvas whose
    // size is driven, a mirror that changes shape for each viewer, say. -xlinka
    public readonly Sync<float2> Size = new();

    // A fingertip pushed through the face presses whatever it lands on (CanvasTouchSource). Off for a
    // panel that only wants the laser: one standing where hands pass through it, or one whose buttons are
    // too small to poke. -xlinka
    public readonly Sync<bool> AcceptPhysicalTouch = new(true);

    // Metres past which this canvas stops drawing. 0 = draws at any distance, which is the default and what
    // a dashboard held in front of your face wants. A panel standing in a world is a different thing: nobody
    // reads 15 px text from forty metres away, but the chunks are still meshed, sorted and drawn, and a
    // showcase world with two dozen panels in it pays for all of them from the far side of the map. The
    // distance goes on every chunk renderer this canvas owns, including the ones built later, and the
    // renderer does the test Godot was already doing for frustum culling. -xlinka
    public readonly Sync<float> MaxViewDistance = new();

    // How a slot's layout elements combine into its sizes. Off: the highest priority element answers every
    // metric alone. On: each metric comes from the highest priority element that gives it and same-priority
    // answers meet at the larger, a size fitter answers for the axes it fits, a metric nobody gives is the
    // rect's own authored size, and a row averages its children's cross-axis flexibility. Panels brought in
    // from a package are laid out by these rules. -xlinka
    public readonly Sync<bool> PerMetricPriority = new();

    // Dissolve band in front of the cut, so a panel fades out over the last three metres rather than
    // popping off as you walk backwards.
    public const float ViewDistanceFadeMargin = 3f;

    // Global kill-switch for GPU stencil masking (per-Mask opt-in via Mask.StencilMasking). On by default;
    // set false to force every mask back to the rectangular clip path. -xlinka
    public static bool StencilMaskingEnabled = true;

    // Scroll by sliding the content's mesh chunk via the shader clip_offset uniform (which moves the vertex AND
    // the clip test in lockstep - see UI_Unlit/UI_Text/UI_TextOverlay), instead of mutating the content rect. The
    // model is split ownership: the content's SIZE lives in its rect (owned by ContentSizeFitter) and its POSITION
    // lives in the chunk offset (owned by ScrollRect) - two separate fields, so a size-fitter and the scroll can
    // never fight over one rect (that shared-field conflict was the old "re-dirty freeze"). A ScrollRect's content
    // gets its own GraphicChunkRoot and scrolls via GraphicChunkRoot.RenderOffset -> SetClipOffset (see ComputeChunk
    // + ScrollRect.ApplyScroll + ApplyScrollOffset). Always on. NOTE the old "parked off, didn't visually move"
    // note was a MISDIAGNOSIS: the content wasn't moving because a per-frame content re-mesh kept resetting
    // clip_offset to 0 (the shared source materials have ClipOffset=0) - the propagation itself works. -xlinka
    public static readonly bool ScrollRenderOffset = true;

    // Cull margin every chunk gets, in CANVAS UNITS - the space the chunk mesh is actually built in.
    //
    // This used to be a single "4" described as four metres, and it bought nothing. The margin grows the
    // instance's LOCAL bounding box and the node transform is applied afterwards, so on a panel at the
    // usual 0.001 slot scale those four units were four millimetres. That mattered because every row of a
    // list is its OWN chunk, baked at its canvas-absolute rect and then drawn somewhere else entirely by
    // the clip_offset uniform: its cull box stays where the row would sit at scroll zero, metres from the
    // pixels. Godot then does exactly what it is supposed to - the box leaves the frustum, the row stops
    // drawing - which is the "rows disappear when I look at the panel from below" bug, and why the
    // separators survived it (they live in the container's own chunk, whose box spans the whole content).
    // The real bound on the displacement is the scroll range, which can never exceed the content's own
    // size, so that is what ChunkCullMargin adds up. The slack on top also keeps a flat panel's box from
    // being exactly zero-thickness. -xlinka
    private const float ChunkCullSlack = 8f;

    // Per-chunk rendering: each GraphicChunkRoot below the canvas owns an independent mesh, so a
    // subtree that animates (e.g. the FPS sparkline) re-uploads only its own small mesh instead of
    // the whole canvas. The root chunk is everything not under a nested GraphicChunkRoot. - xlinka
    private readonly Dictionary<GraphicChunkRoot, GraphicsChunk> _chunkMap = new();
    private readonly Dictionary<GraphicChunkRoot, bool> _chunkBuilt = new();
    // Canvas-space rect each chunk was last MESHED at. Geometry is canvas-absolute, so a chunk whose
    // computed rect moved (a sibling above it changed size) has stale vertices and must re-mesh; one
    // whose rect is unchanged does NOT, even when a structural change elsewhere on the canvas fired.
    // This is what lets rebuilding ONE inspector pane leave the other pane (and its 60 row chunks)
    // untouched instead of re-tessellating the whole canvas (the "everything flashes" bug). -xlinka
    private readonly Dictionary<GraphicChunkRoot, Rect> _chunkMeshedRect = new();
    private readonly HashSet<GraphicChunkRoot> _dirtyChunks = new();
    // This cycle's captured dirty set: _dirtyChunks is cleared BEFORE the prepare pass so dirt
    // raised DURING prepare survives to the next cycle instead of being wiped after the fact.
    private readonly HashSet<GraphicChunkRoot> _cycleDirtyChunks = new();
    // Chunks whose subtree layout (not just mesh) needs recomputing - scoped layout for
    // self-contained changes (e.g. a slider handle) so we don't re-lay-out the whole canvas.
    private readonly HashSet<GraphicChunkRoot> _layoutDirtyChunks = new();
    // Chunks whose material clip window had a LIVE scroll offset folded into it at bake (only happens for a
    // scroll nested inside another scroll - see ComputeInheritedClips). That window is right only for the
    // offset it was baked at, so these must re-mesh when the ancestor scroll moves; every other chunk keeps
    // the cheap uniform-only scroll path. -xlinka
    private readonly HashSet<GraphicChunkRoot> _offsetBakedClipChunks = new();
    private readonly HashSet<GraphicChunkRoot> _seenChunkRoots = new();
    // Same chunks as _seenChunkRoots but kept in DISCOVERY (tree) order, so we can hand each chunk a
    // monotonically increasing SortingOrder in hierarchy order -> later-in-tree chunks tie-break on top. -xlinka
    private readonly List<GraphicChunkRoot> _chunkOrder = new();
    private readonly List<GraphicChunkRoot> _chunkScratch = new();
    // Companion set for the worklist: dedupe was List.Contains inside the drain loop = O(n^2) in
    // chunk count, which per-row chunking (70+ chunks per inspector panel) turned into real cost.
    private readonly HashSet<GraphicChunkRoot> _chunkScratchSet = new();

    // Canvas-wide shared materials: one text material per atlas / one property block per texture for
    // the WHOLE canvas. These lived per-chunk, which multiplied them by the chunk count once per-row
    // chunk roots landed. -xlinka
    private readonly Dictionary<TextureAsset, UITextMaterial> _sharedTextMaterials = new();
    private readonly Dictionary<IAssetProvider<TextureAsset>, MainTexturePropertyBlock> _sharedImageBlocks = new();
    private readonly Dictionary<IAssetProvider<TextureAsset>, UIUnlitMaterial> _sharedImageMaterials = new();
    private readonly Dictionary<IAssetProvider<TextureAsset>, UIUnlitMaterial> _sharedStillImageMaterials = new();

    // A canvas whose content never scrolls can draw from the world's shared UI materials (SharedUIMaterials)
    // instead of minting its own: its text, image and plain materials and their per-surface priority clones
    // come from one pool. Set before it first draws. A scrolling canvas must not: a scroll writes its clip
    // offset into its clones, and shared clones would carry one canvas's scroll onto every other's. -xlinka
    public bool SharesMaterials { get; set; }

    // All text drawn from the same atlas shares one material -> one submesh per atlas (per clip). - xlinka
    internal UITextMaterial GetSharedTextMaterial(TextureAsset atlas)
    {
        if (SharesMaterials && World is { } world)
            return SharedUIMaterials.Of(world).Text(atlas, this);
        if (!_sharedTextMaterials.TryGetValue(atlas, out var material) || material.IsDestroyed)
        {
            material = Slot.AddLocalSlot("TextMaterial").AttachComponent<UITextMaterial>();
            SetUpTextMaterial(material, atlas);
            _sharedTextMaterials[atlas] = material;
        }
        else if (TextMaterialStale(material, atlas))
            SetUpTextMaterial(material, atlas);
        return material;
    }

    // The atlas only knows it holds a distance field once its first glyph is in, so a material made before
    // that has the wrong text path; drawn that way every glyph quad fills solid. -xlinka
    internal static bool TextMaterialStale(UITextMaterial material, TextureAsset atlas)
        => material.UseMSDF.Value != atlas.IsMSDF || (atlas.IsMSDF && material.PixelRange.Value != atlas.MsdfPixelRange);

    internal void SetUpTextMaterial(UITextMaterial material, TextureAsset atlas)
    {
        material.DirectTexture = atlas;
        // Reconstruct glyphs from the distance field when the atlas is MSDF; otherwise the shader falls back
        // to coverage. The atlas carries this so we don't need the FontAsset here. -xlinka
        material.UseMSDF.Value = atlas.IsMSDF;
        if (atlas.IsMSDF)
            material.PixelRange.Value = atlas.MsdfPixelRange;
        if (DrawsOverlay)
            material.ZTest.Value = ZTest.Always;
        ApplyTextStyle(material);
        material.ForceUpdate();
    }

    private void ApplyTextStyle(UITextMaterial material)
    {
        material.OutlineColor.Value = TextOutlineColor.Value;
        material.OutlineThickness.Value = TextOutlineThickness.Value;
        material.FaceDilate.Value = TextFaceDilate.Value;
        material.FaceSoftness.Value = TextFaceSoftness.Value;
        material.UnderlayColor.Value = TextUnderlayColor.Value;
        material.UnderlayOffset.Value = TextUnderlayOffset.Value;
        material.UnderlaySoftness.Value = TextUnderlaySoftness.Value;
    }

    private void UpdateTextStyle()
    {
        // A sharing canvas's text material is keyed by its style, so a new style is a new material: draw again.
        if (SharesMaterials)
        {
            MarkDirty();
            return;
        }
        foreach (var material in _sharedTextMaterials.Values)
        {
            if (material.IsDestroyed) continue;
            ApplyTextStyle(material);
            material.ForceUpdate();
        }
        _renderRequested = true;
    }

    // All images using the same texture share one property block -> one submesh per texture. - xlinka
    internal MainTexturePropertyBlock GetSharedImageBlock(IAssetProvider<TextureAsset> texture)
    {
        if (SharesMaterials && World is { } world)
            return SharedUIMaterials.Of(world).Block(texture);
        if (!_sharedImageBlocks.TryGetValue(texture, out var block) || block.IsDestroyed)
        {
            ForgetDestroyedTextures(_sharedImageBlocks);
            block = Slot.AddLocalSlot("ImageBlock").AttachComponent<MainTexturePropertyBlock>();
            block.Texture.Target = texture;
            _sharedImageBlocks[texture] = block;
        }
        return block;
    }

    // All images using the same texture share one material, with the texture carried ON the material
    // (like GetSharedTextMaterial) rather than a property block. The per-surface clone then samples the
    // texture directly, so live uniform writes - the scroll clip_offset - reach it. A property-block variant
    // is cloned once and cached, so it silently misses those writes and the image freezes while text scrolls. -xlinka
    // still: an animated picture held on its first frame, a material of its own beside the one that plays it.
    internal UIUnlitMaterial GetSharedImageMaterial(IAssetProvider<TextureAsset> texture, bool still = false)
    {
        if (SharesMaterials && World is { } world)
            return SharedUIMaterials.Of(world).Image(texture, DrawsOverlay, Culling.Value, still);
        var shared = still ? _sharedStillImageMaterials : _sharedImageMaterials;
        if (shared.TryGetValue(texture, out var material) && !material.IsDestroyed
            && material.Culling.Value != Culling.Value)
        {
            material.Culling.Value = Culling.Value;
            material.ForceUpdate();
        }
        if (!shared.TryGetValue(texture, out material) || material.IsDestroyed)
        {
            ForgetDestroyedTextures(shared);
            material = Slot.AddLocalSlot("ImageMaterial").AttachComponent<UIUnlitMaterial>();
            material.Texture.Target = texture;
            material.PlayFrames.Value = !still;
            material.Culling.Value = Culling.Value;
            material.ZWrite.Value = ZWrite.Off;
            material.RenderQueue.Value = 3000;
            if (DrawsOverlay)
                material.ZTest.Value = ZTest.Always;
            material.ForceUpdate();
            shared[texture] = material;
        }
        return material;
    }

    // A rebuilt row with a picture brings a texture provider of its own, and the old one's shared material
    // outlived it: a material panel rebuilt six times held nine. Swept whenever a new texture comes in, so
    // these stay the size of the textures still shown. Surfaces draw from the chunks' own clones, and a
    // destroyed texture never reaches these lookups again, so nothing reads a swept one. -xlinka
    private static void ForgetDestroyedTextures<T>(Dictionary<IAssetProvider<TextureAsset>, T> shared) where T : Component
    {
        List<IAssetProvider<TextureAsset>>? gone = null;
        foreach (var pair in shared)
        {
            if (pair.Key.IsDestroyed)
                (gone ??= new List<IAssetProvider<TextureAsset>>()).Add(pair.Key);
        }
        if (gone == null)
            return;
        foreach (var texture in gone)
        {
            var holder = shared[texture];
            if (!holder.IsDestroyed)
                holder.Slot.Destroy();
            shared.Remove(texture);
        }
    }
    // Nested chunks tessellated this rebuild, submitted after the compute pass. Split
    // so the compute (mesh build) can move to a worker while submit/upload stays on
    // the main thread (Godot mesh/material ops are main-thread only).
    private readonly List<GraphicChunkRoot> _computedChunks = new();
    private bool _rootDirty = true;
    private bool _fullDirty = true;
    private bool _layoutDirty = true;
    // Set after each mesh submit: the captured UI changed, so the offscreen viewport should render ONE frame
    // for it. Consumed by the host (dashboard). This is what lets the UI render on change instead of every
    // single frame. -xlinka
    private bool _renderRequested;
    // An animated picture plays on this canvas (the fastest one's frames a second, 0 for none), and when the host
    // last captured for it. Worked out after each submit from what the chunks drew.
    private float _playingFps;
    private double _playedAt;
    // The chunk being rendered rides a scroll offset (it IS scroll content or sits under some). Two effects:
    // clip elision is off (an item fully inside the viewport at bake time scrolls OUT later, and an elided
    // clip would let it spill past the viewport), and a mask INSIDE the chunk clips as a geometry trim
    // instead of a material window - it moves with the geometry, and a material window can't. -xlinka
    private bool _chunkRidesScroll;
    // Set while a chunk's partition walk meets the root of another chunk inside it.
    private bool _partitionFoundChunk;
    // Guards the async rebuild cycle: prepare (main) -> emit (worker) -> submit (main). Only one
    // cycle at a time; a change mid-cycle leaves its dirty flag set so the next OnCommonUpdate
    // re-runs. Touched only on the main thread (OnCommonUpdate + the RunSynchronously submit
    // callback), so no locking is needed.
    private bool _updateRunning;
    private bool _cycleRenderRoot;
    // Any emit failure this cycle: written on the worker, read on main in FinishRebuildCycle.
    private volatile bool _emitFailed;

    // Lift given to an overlay-level chunk that sits under no StackLayer: a modal hung straight on the canvas
    // root has to clear every lifted sheet, or their depth-writing backings hide it. Zero leaves the canvas
    // flat, which is every canvas that stacks nothing. -xlinka
    public float OverlayLift { get; set; }

    private bool _placementDirty;

    // A StackLayer moved or rose. Only chunk transforms change: no layout, no mesh.
    public void MarkPlacementDirty() => _placementDirty = true;

    // A press began here, with what it landed on (null when nothing took it), before that element acts on it.
    public event Action<IUIInteractable?, UIInteractionContext>? PressBegan;

    // Full refresh: recompute layout and rebuild the root chunk and every nested chunk. - xlinka
    public void MarkDirty()
    {
        _fullDirty = true;
        _layoutDirty = true;
    }

    // A layout-affecting change happened (rect/anchor/offset, layout component, structure,
    // scroll). Recompute rects and re-render everything, since moved chunks aren't individually
    // tracked. Pure visual changes go through MarkDirty(rect) instead, which re-meshes only the
    // touched chunk and reuses cached rects - that's what keeps hover/press off the layout path.
    public void MarkLayoutDirty()
    {
        _layoutDirty = true;
        _fullDirty = true;
    }

    // A slot's active state changed (content shown/hidden) - NOT a content change. Runs the chunk
    // reconcile (re-enable newly-visible persisted chunks, disable hidden ones) + a layout pass,
    // but deliberately does NOT set _fullDirty: built chunks just toggle back on (instant, no
    // re-tessellate, no worker hop), and only genuinely new/changed content re-meshes. This is what
    // makes re-showing content appear immediately instead of popping in a frame late.
    public void MarkVisibilityDirty()
    {
        _rootDirty = true;
        _layoutDirty = true;
    }

    // Scoped visibility change: a free-anchored leaf (e.g. a checkbox dot, a hover overlay) inside a BUILT
    // chunk toggled active. Lay out and re-mesh just that chunk - it re-walks its subtree and picks up the new active
    // state - with NO full-canvas relayout, no root rebuild, and no chunk reconcile (which is what used to
    // disable a panel's nested row chunks and make the content vanish). Falls back to the safe full path if
    // the rect isn't inside a built chunk. Only the caller's layout-participation check decides which to use.
    // -xlinka
    public void MarkVisibilityDirty(RectTransform rect)
    {
        var root = FindChunkRoot(rect.Slot);
        // The scoped path re-meshes ONE built chunk and skips the render-root reconcile (CleanupChunks). That's
        // only safe when the toggled subtree lives entirely inside that one chunk. If it CONTAINS (or IS) a
        // nested chunk root - e.g. a whole dashboard screen whose rows are each their own chunk - those nested
        // chunks are independent and only the render-root path enables/disables them. Take the scoped path only
        // for pure leaf toggles (checkbox dot, hover overlay); otherwise a hidden screen's row chunks stay on
        // screen and overlap the new one. -xlinka
        if (root == null || !_chunkMap.ContainsKey(root) || !(_chunkBuilt.TryGetValue(root, out var built) && built)
            || SubtreeHasChunkRoot(rect.Slot))
        {
            _rootDirty = true;
            _layoutDirty = true;
            return;
        }
        // Layout passes skip a slot while it is off, so one shown again still holds the rect it had when its
        // parent was somewhere else. Its chunk is laid out again (scoped) before it meshes.
        _layoutDirtyChunks.Add(root);
        _dirtyChunks.Add(root);
    }

    // True if this slot or anything in its subtree is a GraphicChunkRoot. Early-outs at the first hit, so it's
    // cheap for the leaf toggles that actually want the scoped path (a checkbox dot's subtree is a slot or two);
    // a screen returns true almost immediately off its own/first row's chunk root. -xlinka
    private bool SubtreeHasChunkRoot(Slot slot)
    {
        if (slot.GetComponent<GraphicChunkRoot>() != null)
            return true;
        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
            if (SubtreeHasChunkRoot(children[i]))
                return true;
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
            if (SubtreeHasChunkRoot(localChildren[i]))
                return true;
        return false;
    }

    // A STRUCTURAL change (component attach/destroy/enable, i.e. a row added or removed): needs a
    // full layout recompute (it can resize this rect and reflow siblings) and a root pass (to discover
    // newly-added nested chunks), but NOT _fullDirty - that re-meshes every chunk on the canvas. The
    // changed chunk is marked dirty here; siblings that actually MOVED are caught by the moved-rect
    // check in the prepare loop, so only genuinely-affected chunks re-tessellate. -xlinka
    public void MarkStructuralDirty(RectTransform rect)
    {
        _layoutDirty = true;
        _rootDirty = true;
        var root = FindChunkRoot(rect.Slot);
        if (root != null && _chunkMap.ContainsKey(root))
            _dirtyChunks.Add(root);
    }

    // Has this chunk's canvas-space position/size changed since it was last meshed? Geometry is
    // canvas-absolute, so a moved chunk must re-mesh; an unmoved one can be reused. -xlinka
    private bool ChunkMoved(GraphicChunkRoot root)
    {
        var rt = root.Slot.GetComponent<RectTransform>();
        if (rt == null)
            return false;
        return !_chunkMeshedRect.TryGetValue(root, out var last) || !last.Equals(rt.LocalComputeRect);
    }

    // Scoped layout: a self-contained change (e.g. a slider handle anchor) inside a built chunk.
    // Re-lay-out only that chunk's subtree and re-mesh that chunk - no whole-canvas layout.
    public void MarkLayoutDirty(RectTransform rect)
    {
        var root = FindChunkRoot(rect.Slot);
        if (root == null || !_chunkMap.ContainsKey(root) || !(_chunkBuilt.TryGetValue(root, out var built) && built))
        {
            // Element lives in the root chunk (no owning nested chunk) or its chunk isn't built yet. Recompute
            // the whole layout (_layoutDirty) but only mark _rootDirty, NOT _fullDirty: the root chunk always
            // re-meshes on a root pass, and every nested chunk whose rect actually moved is caught by
            // ChunkMoved against the freshly recomputed rects. Setting _fullDirty here re-tessellated EVERY
            // chunk on any leaf move (a scrollbar handle nudging its own rect took the whole file grid down
            // with it). Nested-chunk elements never reach this branch - they take the scoped path below. -xlinka
            _layoutDirty = true;
            _rootDirty = true;
            // A KNOWN chunk that just isn't flagged built yet still has to re-mesh on that pass. Text
            // glyphs land mid-cycle (async atlas raster) and re-drive here before submit flips built;
            // without this the next root pass skips the chunk (built, unmoved, not dirty) and the row
            // keeps its glyphless bake until something else touches it - the vanishing-letters bug. -xlinka
            if (root != null && _chunkMap.ContainsKey(root))
                _dirtyChunks.Add(root);
            return;
        }
        _layoutDirtyChunks.Add(root);
        _dirtyChunks.Add(root);
    }

    // Route a change to the chunk that owns it, so only that chunk re-renders/uploads.
    // A chunk that hasn't been built yet (not in _chunkMap) can't be re-rendered by
    // the per-chunk path - it's only discovered/built during a root pass. So escalate
    // to a root rebuild, otherwise its content stays invisible until some unrelated
    // root-dirty event (e.g. a hover) finally builds it. - xlinka
    public void MarkDirty(RectTransform rect)
    {
        var root = FindChunkRoot(rect.Slot);
        if (root == null || !_chunkMap.ContainsKey(root))
            _rootDirty = true;
        else
            _dirtyChunks.Add(root);
    }

    // A rebuild cycle is in flight (prepared on main, worker tessellating, submit pending). External
    // render triggers (the dash's font-atlas pulse) must NOT capture the viewport mid-cycle: old chunks are
    // still visible and new ones aren't meshed yet, so a capture now shows overlap/holes = flicker. -xlinka
    public bool IsRebuildInProgress => _updateRunning;

    // True once after each rebuild/mesh submit. The host (dashboard) polls this each frame to render exactly
    // ONE offscreen frame per change, instead of re-rendering the full-res capture every frame. -xlinka
    public bool ConsumeRenderRequested()
    {
        if (!_renderRequested)
            return false;
        _renderRequested = false;
        return true;
    }

    // Something on the canvas changed its pixels without changing its mesh (a texture updated in place, a
    // desktop or a video), so the host captures one more frame. Mid-rebuild it is left to the cycle's own
    // request at the end, which shows the new pixels too and never catches the canvas half meshed. -xlinka
    public void RequestRender()
    {
        if (!_updateRunning)
            _renderRequested = true;
    }

    public RectTransform? RootRectTransform => _rootRect;

    // Cheap spatial reject for the pointer scan. Every hit area in the canvas is its RectTransform's
    // LocalComputeRect (Graphic/InteractionElement/InteractionBlock all test that rect), so a point outside
    // the union of every active rect cannot hit anything and the whole recursive scan can be skipped.
    // Refreshed once per layout cycle in UpdateCanvasBounds. -xlinka
    private const float HitBoundsMinMargin = 32f;
    private Rect _hitBounds;
    private bool _hitBoundsValid;
    private bool _hitBoundsStale = true;

    private bool PointCouldHit(in float2 localPoint)
    {
        // Refreshed here rather than on every layout pass. A scoped chunk relayout moves rects without
        // running the whole-canvas pass, so the bounds have to be able to go stale; recomputing them at
        // the point something actually asks means a canvas nobody is pointing at never pays the walk.
        if (_hitBoundsStale)
            RefreshHitBounds();
        return !_hitBoundsValid || _hitBounds.Contains(localPoint);
    }

    private void RefreshHitBounds()
    {
        _hitBoundsStale = false;
        if (_rootRect == null)
        {
            _hitBoundsValid = false;
            return;
        }

        _rootRect.UpdateBounds();
        var b = _rootRect.BoundingRect;
        if (b.IsEmpty)
        {
            _hitBoundsValid = false;
            return;
        }

        // Margin, not a tight fit. Content under a scrolling mask is tested in content space with the
        // scroll displacement taken back off, so a hit point can sit slightly outside the raw union of
        // unscrolled rects. Missing a canvas by metres is what this filter is for; anything near the
        // edge falls through to the real scan. -xlinka
        float mx = MathF.Max(b.width * 0.25f, HitBoundsMinMargin);
        float my = MathF.Max(b.height * 0.25f, HitBoundsMinMargin);
        _hitBounds = Rect.FromMinMax(
            new float2(b.Min.x - mx, b.Min.y - my),
            new float2(b.Max.x + mx, b.Max.y + my));
        _hitBoundsValid = true;
    }
    public GraphicChunkRoot? ChunkRoot => _chunkRoot;
    public GraphicsChunk? RootChunk => _rootChunk;
    public int InteractionTargetPriority => 1000;

    public InteractionDescription GetInteractionDescription(InteractionLaser laser)
    {
        return new InteractionDescription
        {
            Name = HoveredTip(GetInteractionSource(laser), GetPointerId(laser)) ?? Slot?.SlotName.Value,
            Cursor = LaserCursor.Default,
        };
    }

    // The authored tip of the element a pointer is on, or null when it has none. Surfaces that feed this
    // canvas a pointer (the dash, popped panels) ask with the same interaction source and pointer id.
    public string? HoveredTip(UIInteractionSource source, int pointerId)
    {
        if (!_pointers.TryGetValue(PointerKey(source, pointerId), out var state))
            return null;
        if (state.Hovered is not InteractionElement { IsDestroyed: false } element)
            return null;
        var tip = element.Tip.Value;
        return string.IsNullOrEmpty(tip) ? null : tip;
    }

    public bool TryGetLaserDragPoint(InteractionLaser laser, in float3 rayOrigin, in float3 rayDirection, out float3 point)
    {
        point = default;
        if (!TryRayToCanvasPoint(rayOrigin, rayDirection, out var context, GetInteractionSource(laser), GetPointerId(laser), GetInteractionActor(laser)))
            return false;
        point = context.WorldPoint;
        return true;
    }

    public bool TryGetLaserPointerHit(
        InteractionLaser laser,
        in float3 rayOrigin,
        in float3 rayDirection,
        float maxDistance,
        out LaserPointerHit hit)
    {
        hit = default;
        if (!AcceptsPointerFrom(GetInteractionActor(laser)) || !MightReach(rayOrigin, rayDirection, maxDistance))
        {
            return false;
        }
        var source = GetInteractionSource(laser);
        int pointerId = GetPointerId(laser);

        // Range and bounds are checked BEFORE the subtree scan, not after. The old order built the ray
        // context, walked every slot in the canvas, and only then threw the result away for being out of
        // the laser's reach - so every canvas in the world whose plane the ray crossed paid a full scan,
        // per hand, per frame. -xlinka
        if (!TryRayToCanvasPoint(rayOrigin, rayDirection, out var context, source, pointerId, GetInteractionActor(laser)))
        {
            return false;
        }

        if (context.Distance > maxDistance || !PointCouldHit(context.LocalPoint))
        {
            return false;
        }

        var candidate = default(HitCandidate);
        ScanHitSlot(Slot, in context, ref candidate, null, float2.Zero);
        if (candidate.Interactable == null && !IsSurfaceAt(Slot, in context.LocalPoint, null, float2.Zero))
        {
            return false;
        }

        hit = new LaserPointerHit(context.Distance, context.WorldPoint);
        return true;
    }

    // Whether the canvas has anything drawn (or a blocker) under the point. The beam stops on a panel's
    // backing as well as on its controls: a panel that only caught the beam on its buttons let it through
    // to the wall behind in every gap between them. Where nothing is drawn it still passes. Same active,
    // clip and scroll rules as the hit scan. -xlinka
    private bool IsSurfaceAt(Slot slot, in float2 canvasPoint, Rect? clipRect, float2 scroll)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
            return false;
        if (clipRect.HasValue && !clipRect.Value.Contains(canvasPoint))
            return false;

        scroll += SlotHitStep(slot);
        var point = canvasPoint - scroll;
        var rect = slot.GetComponent<RectTransform>();
        var mask = slot.GetComponent<Mask>();
        bool isMask = mask != null && mask.Enabled.Value && rect != null;
        if (rect != null && rect.LocalComputeRect.Contains(point))
        {
            var comps = slot.Components;
            for (int ci = 0; ci < comps.Count; ci++)
            {
                if (comps[ci] is Graphic graphic && graphic.Enabled.Value && (!isMask || mask!.ShowMaskGraphic.Value))
                    return true;
                if (comps[ci] is InteractionBlock block && block.BlocksPoint(point))
                    return true;
            }
        }

        var nextClip = clipRect;
        if (isMask)
        {
            var maskRect = Translate(rect!.LocalComputeRect, scroll);
            nextClip = nextClip.HasValue ? nextClip.Value.Intersection(maskRect) : maskRect;
            if (nextClip.Value.IsEmpty)
                return false;
        }

        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (IsSurfaceAt(children[i], in canvasPoint, nextClip, scroll))
                return true;
        }
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
        {
            if (IsSurfaceAt(localChildren[i], in canvasPoint, nextClip, scroll))
                return true;
        }
        return false;
    }

    // A headset beam on an element's edge keeps that element until it is clearly past it, so hand tremor
    // does not blink the highlight between neighbours or move a press onto the one next door. The margin is
    // an angle at the beam's distance. A mouse and a fingertip are exact and get none. Off its edge only: a
    // beam still inside it is on whatever the scan found there, a button in the window it was resting on or
    // a window in front, and holding then kept the hover and the press on the body behind. Nor when the
    // scan found another control: the hold reaches over a key gap, and must never keep the last key while
    // the beam already sits on the next one. -xlinka
    private const float HoverHoldAngle = 0.5f * MathF.PI / 180f;

    private bool HoldsHover(IUIInteractable held, IUIInteractable? found, in UIInteractionContext context)
    {
        if (found != null && !ReferenceEquals(found, held) && IsPressable(found))
            return false;
        if (context.Source is not (UIInteractionSource.VRLeft or UIInteractionSource.VRRight))
            return false;
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive)
            return false;
        if (!held.CanInteract || held is not Component { Slot: { } heldSlot })
            return false;
        float scale = MathF.Abs(Slot.GlobalScale.x);
        if (scale <= 0.000001f)
            return false;
        float margin = context.Distance * HoverHoldAngle / scale;
        var point = CanvasPointToLocal(heldSlot, context.LocalPoint);
        if (held.IsPointInside(point))
            return false;
        if (held.IsPointInside(point + new float2(margin, 0f)) || held.IsPointInside(point - new float2(margin, 0f))
            || held.IsPointInside(point + new float2(0f, margin)) || held.IsPointInside(point - new float2(0f, margin)))
            return true;
        // Diagonals, for a beam off a corner, where two gaps cross.
        float d = margin * 0.70710678f;
        return held.IsPointInside(point + new float2(d, d)) || held.IsPointInside(point + new float2(-d, d))
            || held.IsPointInside(point + new float2(d, -d)) || held.IsPointInside(point - new float2(d, d));
    }

    // Something a press lands on to do a job, as against a body, a bar or a list it sits in.
    private static bool IsPressable(IUIInteractable element)
        => element is InteractionElement { ClickControl: true } or Slider or TextInput or Pad2D;

    // How far a press may wander before it counts as a drag, in this canvas's units. A headset beam takes
    // it as an angle at the beam's distance, sized as the hover hold is: a squeeze jolts the aim by an angle
    // whatever the panel's scale. A mouse and a fingertip keep the plain unit count.
    private const float DragSlopAngle = 2f * MathF.PI / 180f;

    private float DragSlop(in UIInteractionContext context)
    {
        if (context.Source is UIInteractionSource.VRLeft or UIInteractionSource.VRRight)
        {
            var input = Engine.Current?.InputInterface;
            float scale = MathF.Abs(Slot.GlobalScale.x);
            if ((input == null || input.IsVRActive) && scale > 0.000001f && context.Distance > 0f)
                return context.Distance * DragSlopAngle / scale;
        }
        return InteractionElement.DragPassThreshold;
    }

    // A release-acting click whose beam slid off it during the squeeze, but not far enough to be a drag.
    private bool ReleasedInPlace(PointerState state, IUIInteractable pressed, in UIInteractionContext context)
    {
        if (state.Transferred || pressed is not InteractionElement { ClickControl: true })
            return false;
        var travel = context.LocalPoint - state.PressPoint;
        float slop = DragSlop(in context);
        return MathF.Abs(travel.x) <= slop && MathF.Abs(travel.y) <= slop;
    }

    public void UpdateLaserPointer(
        InteractionLaser laser,
        int pointerId,
        in float3 rayOrigin,
        in float3 rayDirection,
        bool isPressed)
    {
        UpdatePointer(GetInteractionSource(laser), pointerId, rayOrigin, rayDirection, isPressed, GetInteractionActor(laser));
    }

    public void ClearLaserPointer(InteractionLaser laser, int pointerId)
    {
        ClearPointer(GetInteractionSource(laser), pointerId, GetInteractionActor(laser));
    }

    public bool ProcessLaserAxis(InteractionLaser laser, int pointerId, in float2 axis)
    {
        return ProcessAxis(GetInteractionSource(laser), pointerId, axis);
    }

    public bool WantsLaserAxis(InteractionLaser laser, int pointerId)
        => WantsAxis(GetInteractionSource(laser), pointerId);

    public bool TriggerLaserSecondary(InteractionLaser laser, int pointerId)
    {
        return TriggerSecondary(GetInteractionSource(laser), pointerId);
    }

    public UIHit? HitTest(float3 rayOrigin, float3 rayDirection)
    {
        return TryHitTest(rayOrigin, rayDirection, out var hit) ? hit : null;
    }

    // resolves the deepest active rect under the ray, interactable or not. grip interactions use this
    // to find the ROW under the pointer (labels and plain containers are not IUIInteractable, so the
    // press-oriented hit test can't see them) and then walk its parents for grab-time behaviors
    public bool TryResolveUISlot(float3 rayOrigin, float3 rayDirection, out Slot? slot, out float3 worldPoint)
    {
        slot = null;
        worldPoint = float3.Zero;
        if (!AcceptsPointerFrom(null) || !TryRayToCanvasPoint(rayOrigin, rayDirection, out var context, UIInteractionSource.Unknown, 0))
        {
            return false;
        }
        worldPoint = context.WorldPoint;
        slot = FindDeepestRectAt(Slot, context.LocalPoint, null, float2.Zero);
        return slot != null;
    }

    // Depth-first, honoring active state and mask clips like ScanHitSlot; later siblings win ties the
    // same way later-drawn content renders on top. Scroll composition is identical to ScanHitSlot - see the
    // two-space rule documented there.
    private Slot? FindDeepestRectAt(Slot slot, in float2 canvasPoint, Rect? clipRect, float2 scroll)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return null;
        }
        if (clipRect.HasValue && !clipRect.Value.Contains(canvasPoint))
        {
            return null;
        }

        scroll += SlotHitStep(slot);
        var point = canvasPoint - scroll;

        var rect = slot.GetComponent<RectTransform>();
        var nextClip = clipRect;
        var mask = slot.GetComponent<Mask>();
        if (mask != null && mask.Enabled.Value && rect != null)
        {
            var maskRect = Translate(rect.LocalComputeRect, scroll);
            nextClip = nextClip.HasValue
                ? nextClip.Value.Intersection(maskRect)
                : maskRect;
            if (nextClip.Value.IsEmpty)
            {
                return null;
            }
        }

        Slot? deepest = null;
        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var found = FindDeepestRectAt(children[i], in canvasPoint, nextClip, scroll);
            if (found != null)
            {
                deepest = found;
            }
        }
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
        {
            var found = FindDeepestRectAt(localChildren[i], in canvasPoint, nextClip, scroll);
            if (found != null)
            {
                deepest = found;
            }
        }
        if (deepest != null)
        {
            return deepest;
        }

        return rect != null && rect.LocalComputeRect.Contains(point) ? slot : null;
    }

    public bool TryHitTest(float3 rayOrigin, float3 rayDirection, out UIHit hit)
    {
        return TryHitTest(rayOrigin, rayDirection, UIInteractionSource.Unknown, 0, out hit);
    }

    public bool TryHitTest(
        float3 rayOrigin,
        float3 rayDirection,
        UIInteractionSource source,
        int pointerId,
        out UIHit hit,
        User? actor = null)
    {
        hit = default;
        if (!AcceptsPointerFrom(actor) || !TryRayToCanvasPoint(rayOrigin, rayDirection, out var context, source, pointerId, actor))
        {
            return false;
        }

        if (!PointCouldHit(context.LocalPoint))
        {
            return false;
        }

        var candidate = default(HitCandidate);
        ScanHitSlot(Slot, in context, ref candidate, null, float2.Zero);
        if (candidate.Interactable == null)
        {
            return false;
        }

        hit = new UIHit(candidate.Interactable, context);
        return true;
    }

    // pressBegan: whether the press went down this frame. Left null, the pointer's hand answers it (see
    // PressBeganNow); a caller with no hand behind it can say.
    public void UpdatePointer(
        UIInteractionSource source,
        int pointerId,
        float3 rayOrigin,
        float3 rayDirection,
        bool isPressed,
        User? actor = null,
        bool? pressBegan = null)
    {
        if (!AcceptsPointerFrom(actor) || !TryRayToCanvasPoint(rayOrigin, rayDirection, out var context, source, pointerId, actor))
        {
            ClearPointer(source, pointerId, actor);
            return;
        }

        // Skip the scan, NOT the context: a pointer that presses inside and drags off the panel keeps
        // driving its pressed element below, so this must fall through with hovered = null rather than
        // clearing the pointer outright. Out of bounds the scan could only have returned null anyway. -xlinka
        var candidate = default(HitCandidate);
        _areaScratch.Clear();
        if (PointCouldHit(context.LocalPoint))
        {
            _areaScan = _areaScratch;
            try
            {
                ScanHitSlot(Slot, in context, ref candidate, null, float2.Zero);
            }
            finally
            {
                _areaScan = null;
            }
        }
        var hovered = candidate.Interactable;
        int key = PointerKey(source, pointerId);

        bool known = true;
        if (!_pointers.TryGetValue(key, out var state))
        {
            known = false;
            state = new PointerState();
            _pointers[key] = state;
        }
        var previousPoint = state.LastContext.LocalPoint;
        state.LastContext = context;

        // A finger on this panel owns that hand's press: the laser from the same hand keeps hovering but
        // cannot press, and a press it already had is dropped without a submit, or the trigger squeezed
        // while poking would fire whatever the beam happens to be resting on. The drop happens the frame
        // the poke lands, not on the laser's next update: an element carries one pressed flag, and a
        // late laser release would clear the press the finger is still holding. -xlinka
        if (state.PressSuppressed)
        {
            if (isPressed)
                isPressed = false;
            else
                state.PressSuppressed = false;
        }
        if (isPressed && TouchPressSuppressesLaser(source))
        {
            isPressed = false;
            CancelPress(state, in context);
        }
        else if (source == UIInteractionSource.Touch && isPressed && !state.IsPressed)
        {
            CancelLaserPress(CanvasTouchSource.SideOf(pointerId), in context);
        }

        if (state.Hovered != null && !ReferenceEquals(state.Hovered, hovered) && HoldsHover(state.Hovered, hovered, in context))
            hovered = state.Hovered;
        if (!ReferenceEquals(state.Hovered, hovered))
        {
            state.Hovered?.NotifyHoverExit(in context);
            state.Hovered = hovered;
            // Arrived: the pointer was outside it last update. One that turned up under a pointer sitting
            // still (a ring opening on the hand, a page swap, a list scrolled or rebuilt under the beam)
            // did not, and does not tick the hand.
            if (state.Hovered is InteractionElement entered)
                entered.NotifyHoverEnter(in context, known && !entered.IsPointInside(CanvasPointToLocal(entered.Slot, previousPoint)));
            else
                state.Hovered?.NotifyHoverEnter(in context);
        }
        state.Hovered?.NotifyHoverMove(in context);
        UpdateAreas(state, key, _areaScratch);

        bool deepPress = isPressed && state.IsPressed && pressBegan == null && StepDeepTrigger(state, source, hovered, in context);

        if (isPressed && (!state.IsPressed || deepPress))
        {
            // Buttons act as the press lands, so a press acts only where it went down. One that was already
            // held when it reached this canvas (a panel opened under the trigger that opened it, a beam swung
            // in mid-press, a carry let go with the trigger still down) presses nothing until let go. -xlinka
            bool began = deepPress || (pressBegan ?? PressBeganNow(source, pointerId, null));
            state.Pressed = began ? hovered : null;
            state.PressPoint = context.LocalPoint;
            state.Transferred = false;
            state.HandedOff = false;
            state.DeepRearmed = false;
            // The canvas hears of the press before the element acts on it: a dash window brought to the front
            // by the press would otherwise come back up after its own Close or Minimize had put it away.
            if (began)
            {
                TextInput.PressedAt((hovered as Component)?.Slot ?? Slot, actor);
                PressBegan?.Invoke(hovered, context);
                state.Pressed?.NotifyPress(in context);
            }
        }
        else if (isPressed && state.Pressed != null)
        {
            // An element that stops taking input while held (a button that switches itself off the moment it is
            // pressed, so the slider under it gets the drag) lets go without a submit, and whatever the pointer is
            // over now takes the press up from here, held but never acted on. One that was destroyed, or whose
            // page went away (a press that opened something in its place), drops it. -xlinka
            if (!state.Pressed.CanInteract && state.Pressed is not Component { IsDestroyed: true })
            {
                var lost = state.Pressed;
                lost.NotifyRelease(in context);
                bool steppedAside = lost is Component { Slot.IsActive: true };
                state.Pressed = steppedAside && hovered != null && !ReferenceEquals(hovered, lost) ? hovered : null;
                state.PressPoint = context.LocalPoint;
                state.Transferred = true;
                state.HandedOff = true;
                state.Pressed?.NotifyPressTakenOver(in context);
            }

            // Arbitration: a pressed child that opts into pass-through (a button/list item) relinquishes the
            // drag to the nearest scrolling ancestor once the pointer has moved past the threshold, so the
            // list scrolls instead of the child swallowing the gesture. A slider passes only cross-axis drags
            // and keeps its own. Done once per gesture. The travel is scaled so the element's unit threshold
            // reads as this pointer's slop (see DragSlop). -xlinka
            if (!state.Transferred && state.Pressed is InteractionElement pressedElement)
            {
                var dragDelta = (context.LocalPoint - state.PressPoint) * (InteractionElement.DragPassThreshold / DragSlop(in context));
                if (pressedElement.PassDragToParent(in dragDelta))
                {
                    var scroll = FindScrollAncestor(pressedElement);
                    if (scroll != null)
                    {
                        pressedElement.NotifyRelease(in context); // give up the press, no submit
                        state.Pressed = scroll;
                        state.Transferred = true;
                        scroll.NotifyPressTakenOver(in context);  // re-anchors the scroll at the current point
                    }
                }
            }
            state.Pressed?.NotifyDrag(in context);
        }
        else if (!isPressed && state.IsPressed)
        {
            var pressed = state.Pressed;
            pressed?.NotifyRelease(in context);

            if (pressed != null && !state.HandedOff && (ReferenceEquals(pressed, hovered) || ReleasedInPlace(state, pressed, in context)))
            {
                pressed.NotifySubmit(in context);
            }

            state.Pressed = null;
            state.DeepRearmed = false;
        }

        state.IsPressed = isPressed;
    }

    // A finger resting the trigger past its press point holds one long press, and a full squeeze from there
    // crosses no edge, so it clicked nothing. The trigger's deep edges stand in for one, on a press that is
    // only a click (a button or toggle, a bare element, or nothing at all). Easing off the deep level lets go
    // of it the way letting go of the trigger would. Going deep again, after easing off or from a still
    // rest (TriggerLatch decides which), drops whatever is held with no submit and presses what the beam is
    // on now, wherever it moved meanwhile. A press that became a drag (handed to a scroll, or on a slider, a
    // window bar or a resize grip) ignores both edges: a drag held at full pull must not drop when the
    // finger eases off the click. Returns whether a fresh press lands this update. -xlinka
    private bool StepDeepTrigger(PointerState state, UIInteractionSource source, IUIInteractable? hovered, in UIInteractionContext context)
    {
        var (deepPressed, deepReleased) = DeepTriggerEdges(source);
        if (!deepPressed && !deepReleased)
            return false;
        if (!state.DeepRearmed && (state.Transferred || state.HandedOff
            || state.Pressed != null && state.Pressed is not InteractionElement { PressIsClick: true }))
            return false;
        // Going deep from a rest on the element that rest already pressed adds nothing: that press acted (or
        // acts on release) where it landed, and pressing again would flip a toggle straight back. A second
        // click on purpose eases off the deep level first, which re-arms. -xlinka
        if (deepPressed && !state.DeepRearmed && state.Pressed != null && ReferenceEquals(state.Pressed, hovered))
            return false;
        ulong update = World?.Time.UpdateIndex ?? 0UL;
        if (state.DeepUpdate == update)
            return false;
        state.DeepUpdate = update;

        var pressed = state.Pressed;
        state.Pressed = null;
        pressed?.NotifyRelease(in context);
        if (deepReleased)
        {
            if (pressed != null && (ReferenceEquals(pressed, hovered) || ReleasedInPlace(state, pressed, in context)))
                pressed.NotifySubmit(in context);
            state.DeepRearmed = true;
            return false;
        }
        return true;
    }

    // This frame's deep edges of the trigger behind a headset pointer, read the way PressBeganNow reads the
    // press. Off for any other pointer, and for a hand whose primary is not bound to the trigger.
    private static (bool Pressed, bool Released) DeepTriggerEdges(UIInteractionSource source)
    {
        var side = source switch
        {
            UIInteractionSource.VRLeft => Chirality.Left,
            UIInteractionSource.VRRight => Chirality.Right,
            _ => Chirality.None,
        };
        if (side == Chirality.None)
            return default;
        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions == null || !actions.HasEvaluated)
            return default;
        var primary = actions.Interaction(side).Primary;
        return (primary.DeepPressed, primary.DeepReleased);
    }

    public bool ProcessAxis(UIInteractionSource source, int pointerId, in float2 axis)
    {
        if (axis == float2.Zero)
        {
            return false;
        }

        int key = PointerKey(source, pointerId);
        if (!_pointers.TryGetValue(key, out var state) || state.Hovered == null)
        {
            return false;
        }

        return DispatchAxis(state.Hovered, in state.LastContext, in axis);
    }

    // Dispatch a wheel/axis to whatever pointer currently has a hover, regardless of source. The desktop
    // dashboard hovers the canvas under a VR* source (the laser is the pointer even on desktop), but the
    // raw OS mouse wheel is fed with no matching source - a fixed (Desktop,0) feed finds no hover and the
    // wheel does nothing. This routes it to the element actually under the cursor. -xlinka
    public bool ProcessAxisAnyPointer(in float2 axis)
    {
        if (axis == float2.Zero)
            return false;
        foreach (var state in _pointers.Values)
        {
            if (state.Hovered != null && DispatchAxis(state.Hovered, in state.LastContext, in axis))
                return true;
        }
        return false;
    }

    // Whether an axis from this pointer would move whatever it hovers.
    public bool WantsAxis(UIInteractionSource source, int pointerId)
    {
        if (!_pointers.TryGetValue(PointerKey(source, pointerId), out var state) || state.Hovered == null)
            return false;
        foreach (var receiver in GetInteractionReceivers<IUIAxisActionReceiver>(state.Hovered))
        {
            if (receiver.WantsAxis(in state.LastContext))
                return true;
        }
        return false;
    }

    // The scroll an axis from this pointer would move, for something that drives it by itself.
    public ScrollRect? ScrollUnder(UIInteractionSource source, int pointerId)
    {
        if (!_pointers.TryGetValue(PointerKey(source, pointerId), out var state) || state.Hovered == null)
            return null;
        foreach (var receiver in GetInteractionReceivers<IUIAxisActionReceiver>(state.Hovered))
        {
            if (receiver is ScrollRect scroll && scroll.WantsAxis(in state.LastContext))
                return scroll;
        }
        return null;
    }

    public ScrollRect? ScrollUnderLaser(InteractionLaser laser, int pointerId)
        => ScrollUnder(GetInteractionSource(laser), pointerId);

    // The same answer from a plain hit test that leaves every pointer's state alone, for a pointer that hovers
    // nothing right now (a desktop crosshair resting idle).
    public ScrollRect? ScrollUnderRay(in float3 rayOrigin, in float3 rayDirection)
    {
        if (!TryHitTest(rayOrigin, rayDirection, out var hit) || hit.Interactable == null)
            return null;
        foreach (var receiver in GetInteractionReceivers<IUIAxisActionReceiver>(hit.Interactable))
        {
            if (receiver is ScrollRect scroll && scroll.WantsAxis(in hit.Context))
                return scroll;
        }
        return null;
    }

    public bool TriggerSecondary(UIInteractionSource source, int pointerId)
    {
        int key = PointerKey(source, pointerId);
        if (!_pointers.TryGetValue(key, out var state) || state.Hovered == null)
        {
            return false;
        }

        return DispatchSecondary(state.Hovered, in state.LastContext);
    }

    // Grab-action entry points for pointers. A pointer asks the canvas before it grabs anything physical:
    // the element under it (or an ancestor up to a SearchBlock) that implements IUIGrabbable can hand out a
    // grabbable of its own, which is how a widget leaves the grid. Dropping mirrors it: a receiver under the
    // pointer takes the held items and the pointer keeps them out of the world. Both read the pointer's last
    // hover so the caller does not raycast twice. -xlinka
    public IGrabbable? TryGrab(UIInteractionSource source, int pointerId)
    {
        int key = PointerKey(source, pointerId);
        if (!_pointers.TryGetValue(key, out var state) || state.Hovered == null)
        {
            return null;
        }

        foreach (var receiver in GetInteractionReceivers<IUIGrabbable>(state.Hovered))
        {
            var item = receiver.TryGrab(in state.LastContext);
            if (item != null)
            {
                return item;
            }
        }
        return null;
    }

    public bool TryReceive(IReadOnlyList<IGrabbable> items, UIInteractionSource source, int pointerId)
    {
        if (items == null || items.Count == 0)
        {
            return false;
        }
        int key = PointerKey(source, pointerId);
        if (!_pointers.TryGetValue(key, out var state) || state.Hovered == null)
        {
            return false;
        }

        foreach (var receiver in GetInteractionReceivers<IUIGrabReceiver>(state.Hovered))
        {
            if (receiver.TryReceive(items, in state.LastContext))
            {
                return true;
            }
        }
        return false;
    }

    public void ClearPointer(UIInteractionSource source, int pointerId, User? actor = null)
    {
        int key = PointerKey(source, pointerId);
        if (!_pointers.TryGetValue(key, out var state))
        {
            return;
        }

        // Let go where the pointer was last seen, not at the canvas origin: a window bar dragged off the side
        // of the curved dash ends its drag there. Let go at (0,0), it would land in the middle of the desk.
        var context = state.LastContext.Canvas != null
            ? state.LastContext
            : new UIInteractionContext(this, source, pointerId, float2.Zero, float3.Zero, float3.Zero, float3.Zero, 0f, actor);
        state.Hovered?.NotifyHoverExit(in context);
        state.Pressed?.NotifyRelease(in context);
        UpdateAreas(state, key, null);
        _pointers.Remove(key);
    }

    // The areas this pointer left say so, the ones it came over say so; null leaves them all.
    private static void UpdateAreas(PointerState state, int key, List<HoverArea>? over)
    {
        var areas = state.Areas;
        if (areas != null)
        {
            for (int i = areas.Count - 1; i >= 0; i--)
            {
                if (over != null && over.Contains(areas[i]))
                    continue;
                areas[i].PointerLeft(key);
                areas.RemoveAt(i);
            }
        }
        if (over == null || over.Count == 0)
            return;
        areas = state.Areas ??= new List<HoverArea>();
        for (int i = 0; i < over.Count; i++)
        {
            if (areas.Contains(over[i]))
                continue;
            areas.Add(over[i]);
            over[i].PointerEntered(key);
        }
    }

    // A subtree taken off one canvas and hung under another: an app popped out of the dash onto a panel of
    // its own, and back again, carrying whatever it had open. The old canvas lets go of every pointer, each
    // rect in the subtree forgets the canvas it was registered with so the one it now sits under takes it in
    // on its next pass, and both rebuild in full: the old one drops the chunks it can no longer reach, the
    // new one meshes them. -xlinka
    public static void Move(Slot subtree, Slot newParent)
    {
        if (subtree == null || subtree.IsDestroyed || newParent == null || newParent.IsDestroyed)
            return;
        var from = subtree.GetComponentInParents<Canvas>();
        from?.ReleaseAllPointers();
        subtree.SetParent(newParent, preserveGlobalTransform: false);
        Forget(subtree);
        subtree.GetComponent<RectTransform>()?.MarkChangeDirty();
        from?.MarkDirty();
        var to = newParent.GetComponentInParents<Canvas>();
        if (to != null && !ReferenceEquals(to, from))
            to.MarkDirty();
    }

    private static void Forget(Slot slot)
    {
        slot.GetComponent<RectTransform>()?.SetRegisteredCanvas(null);
        foreach (var child in slot.Children)
            Forget(child);
        foreach (var child in slot.LocalChildren)
            Forget(child);
    }

    // Whether this canvas draws the slot: the first canvas above it is this one.
    private bool Holds(Slot? slot)
    {
        for (var s = slot; s != null; s = s.Parent)
        {
            if (ReferenceEquals(s, Slot))
                return true;
            if (s.GetComponent<Canvas>() != null)
                return false;
        }
        return false;
    }

    // Drop every pointer at once, cancelling any press without a submit. For a canvas that has just been
    // picked up: the trigger that was down on a key when the hand closed must not fire that key on the
    // way out. The pointers re-register themselves on their next update. -xlinka
    public void ReleaseAllPointers()
    {
        if (_pointers.Count == 0)
            return;
        foreach (var pointer in _pointers)
        {
            var context = pointer.Value.LastContext.Canvas != null
                ? pointer.Value.LastContext
                : new UIInteractionContext(this, UIInteractionSource.Unknown, pointer.Key, float2.Zero, float3.Zero, float3.Zero, float3.Zero, 0f);
            pointer.Value.Hovered?.NotifyHoverExit(in context);
            pointer.Value.Pressed?.NotifyRelease(in context);
            UpdateAreas(pointer.Value, pointer.Key, null);
        }
        _pointers.Clear();
    }

    private static void CancelPress(PointerState state, in UIInteractionContext context)
    {
        state.PressSuppressed = true;
        state.DeepRearmed = false;
        if (!state.IsPressed)
            return;
        state.Pressed?.NotifyRelease(in context);
        state.Pressed = null;
        state.IsPressed = false;
    }

    // Lasers hover under VRLeft/1 and VRRight/2 (InteractionLaser.GetPointerId) whichever surface routed
    // them here, the dash included.
    private void CancelLaserPress(Chirality side, in UIInteractionContext context)
    {
        if (side == Chirality.None)
            return;
        var laserSource = side == Chirality.Left ? UIInteractionSource.VRLeft : UIInteractionSource.VRRight;
        int laserId = side == Chirality.Left ? 1 : 2;
        if (_pointers.TryGetValue(PointerKey(laserSource, laserId), out var laser))
            CancelPress(laser, in context);
    }

    private bool TouchPressSuppressesLaser(UIInteractionSource source)
    {
        var side = source switch
        {
            UIInteractionSource.VRLeft => Chirality.Left,
            UIInteractionSource.VRRight => Chirality.Right,
            _ => Chirality.None,
        };
        if (side == Chirality.None)
            return false;
        return _pointers.TryGetValue(PointerKey(UIInteractionSource.Touch, CanvasTouchSource.PointerIdFor(side)), out var touch)
            && touch.IsPressed;
    }

    // Whether a fingertip is pressing this canvas right now, by hand.
    public bool IsTouchPressed(Chirality side)
        => _pointers.TryGetValue(PointerKey(UIInteractionSource.Touch, CanvasTouchSource.PointerIdFor(side)), out var touch) && touch.IsPressed;

    bool ITouchSurface.AcceptsPhysicalTouch => AcceptPhysicalTouch.Value && AcceptsPointerFrom(null);

    // The finger line against the z=0 plane, into the readable face (+Z side), inside the root rect. The
    // distance is the ray's own parameter, so the source reads depth off it in metres whatever the panel
    // scale. -xlinka
    public bool TryTouchProject(in float3 rayOrigin, in float3 rayDirection, float rayLength, out TouchSurfaceHit hit)
    {
        hit = default;
        if (!AcceptsPointerFrom(null))
            return false;
        EnsureRoot();
        if (_rootRect == null || Slot == null || !Slot.IsActive)
            return false;

        var localOrigin = Slot.GlobalPointToLocal(rayOrigin);
        var localDirection = Slot.GlobalDirectionToLocal(rayDirection);
        bool backward = FaceBackward.Value;
        if (backward ? localDirection.z <= 0.000001f : localDirection.z >= -0.000001f)
            return false;

        float t = -localOrigin.z / localDirection.z;
        if (t < 0f || t > rayLength)
            return false;

        var localPoint3 = localOrigin + localDirection * t;
        var localPoint = LocalToCanvas(localPoint3);
        var face = _rootRect.LocalComputeRect;
        if (face.IsEmpty)
        {
            _rootRect.UpdateBounds();
            face = _rootRect.BoundingRect;
        }
        if (face.IsEmpty || !face.Contains(localPoint))
            return false;

        float3 normal = backward ? -Slot.Forward : Slot.Forward;
        float normalLength = normal.Length;
        normal = normalLength > 0.000001f ? normal / normalLength : float3.Forward;
        hit = new TouchSurfaceHit(t, Slot.LocalPointToGlobal(localPoint3), normal, localPoint);
        return true;
    }

    public void UpdateTouchPointer(int pointerId, in TouchSurfaceHit hit, bool pressed, User? actor)
    {
        UpdatePointer(UIInteractionSource.Touch, pointerId, hit.Point + hit.Normal * CanvasTouchSource.PointerLift, -hit.Normal, pressed, actor);
    }

    public void ClearTouchPointer(int pointerId, User? actor)
    {
        ClearPointer(UIInteractionSource.Touch, pointerId, actor);
    }

    // Whether the press a pointer reports went down this frame. A laser and the desktop cursor answer from
    // their hand's primary action, the same one that drives their press. Touch arms itself before it may
    // press, and a pointer with no hand behind it (or no input at all) is taken at its word.
    private static bool PressBeganNow(UIInteractionSource source, int pointerId, bool? stated)
    {
        if (stated.HasValue)
            return stated.Value;
        var side = source switch
        {
            UIInteractionSource.VRLeft => Chirality.Left,
            UIInteractionSource.VRRight => Chirality.Right,
            UIInteractionSource.Desktop => pointerId == 1 ? Chirality.Left : pointerId == 2 ? Chirality.Right : Chirality.None,
            _ => Chirality.None,
        };
        if (side == Chirality.None)
            return true;
        var actions = Engine.Current?.InputInterface?.Actions;
        if (actions == null || !actions.HasEvaluated)
            return true;
        return actions.Interaction(side).Primary.Pressed;
    }

    // Nearest scrolling ancestor of a pressed element, for drag pass-through. ScrollRect is an
    // InteractionElement, so the press transfers to it cleanly (its OnPress re-anchors the scroll). -xlinka
    private static ScrollRect? FindScrollAncestor(InteractionElement from)
    {
        for (var slot = from.Slot?.Parent; slot != null; slot = slot.Parent)
        {
            var scroll = slot.GetComponent<ScrollRect>();
            if (scroll != null && scroll.CanInteract)
                return scroll;
        }
        return null;
    }

    public override void OnAwake()
    {
        base.OnAwake();
        MaxViewDistance.OnChanged += _ => PushViewDistanceToChunks();
        TextFaceDilate.OnChanged += _ => UpdateTextStyle();
        TextFaceSoftness.OnChanged += _ => UpdateTextStyle();
        TextOutlineColor.OnChanged += _ => UpdateTextStyle();
        TextOutlineThickness.OnChanged += _ => UpdateTextStyle();
        TextUnderlayColor.OnChanged += _ => UpdateTextStyle();
        TextUnderlayOffset.OnChanged += _ => UpdateTextStyle();
        TextUnderlaySoftness.OnChanged += _ => UpdateTextStyle();
        Culling.OnChanged += _ => MarkDirty();
        FaceBackward.OnChanged += _ => ApplyFacing();
        PerMetricPriority.OnChanged += _ => RemeasureAll();
    }

    // Every cached metric was combined under the other rule.
    private void RemeasureAll()
    {
        if (Slot == null)
            return;
        foreach (var rect in Slot.GetComponentsInChildren<RectTransform>())
            rect.MarkChangeDirty();
        MarkLayoutDirty();
    }

    // Turned canvases put every chunk half a turn round and read pointers mirrored; the colliders and the
    // hit bounds move with them.
    private void ApplyFacing()
    {
        _rootChunk?.ApplyFacing();
        foreach (var pair in _chunkMap)
            pair.Value.ApplyFacing();
        _hitBoundsStale = true;
        UpdateCanvasBounds();
        _renderRequested = true;
    }

    // Canvas space to the slot's own and back. The canvas lies in the slot's z = 0 plane; a turned one
    // (FaceBackward) runs its X the other way.
    public float3 CanvasToLocal(in float2 point) => new float3(FaceBackward.Value ? -point.x : point.x, point.y, 0f);

    public float2 LocalToCanvas(in float3 local) => new float2(FaceBackward.Value ? -local.x : local.x, local.y);

    public override void OnStart()
    {
        base.OnStart();
        EnsureRoot();
    }

    // Chunks are created and disposed all through a canvas's life, so the distance is stored on the canvas
    // and stamped onto each renderer as it appears (GraphicsChunk.EnsureComponents calls this) rather than
    // pushed once. Setting the sync back to 0 comes through here too and hands every chunk its unbounded
    // range back. -xlinka
    internal void ApplyViewDistance(MeshRenderer renderer)
    {
        if (renderer == null || renderer.IsDestroyed)
            return;
        float distance = MathF.Max(0f, MaxViewDistance.Value);
        float margin = distance > 0f ? ViewDistanceFadeMargin : 0f;
        if (renderer.MaxViewDistance == distance && renderer.ViewDistanceFadeMargin == margin)
            return;
        renderer.MaxViewDistance = distance;
        renderer.ViewDistanceFadeMargin = margin;
        renderer.MarkChangeDirty();
    }

    private void PushViewDistanceToChunks()
    {
        if (_rootChunk?.MeshRenderer != null)
            ApplyViewDistance(_rootChunk.MeshRenderer);
        foreach (var pair in _chunkMap)
        {
            if (pair.Value.MeshRenderer != null)
                ApplyViewDistance(pair.Value.MeshRenderer);
        }
    }

    public override void OnEnabled()
    {
        base.OnEnabled();
        _fullDirty = true;
    }

    // The canvas's own parts are added by the machine that made it. One made elsewhere waits for them to
    // arrive: a canvas built in the middle of an update is sent as that update ends, a frame before its maker
    // starts it and adds them, and a viewer adding its own put a second chunk root on somebody else's slot
    // in an open world and was refused in a frozen one. -xlinka
    public override void OnChanges()
    {
        base.OnChanges();
        ApplySize();
    }

    private void ApplySize()
    {
        var size = Size.Value;
        if (!(size.x > 0f) && !(size.y > 0f))
            return;
        EnsureRoot();
        if (_rootRect == null)
            return;
        var min = _rootRect.OffsetMin.Value;
        var max = _rootRect.OffsetMax.Value;
        var anchorMin = _rootRect.AnchorMin.Value;
        var anchorMax = _rootRect.AnchorMax.Value;
        if (size.x > 0f)
        {
            anchorMin.x = anchorMax.x = 0.5f;
            min.x = -size.x * 0.5f;
            max.x = size.x * 0.5f;
        }
        if (size.y > 0f)
        {
            anchorMin.y = anchorMax.y = 0.5f;
            min.y = -size.y * 0.5f;
            max.y = size.y * 0.5f;
        }
        SetDerived(_rootRect.AnchorMin, anchorMin);
        SetDerived(_rootRect.AnchorMax, anchorMax);
        SetDerived(_rootRect.OffsetMin, min);
        SetDerived(_rootRect.OffsetMax, max);

        if (SizeCollider.Value || Slot.GetComponent<BoxCollider>() is not { } collider)
            return;
        var box = collider.Size.Value;
        var sized = new float3(size.x > 0f ? size.x : box.x, size.y > 0f ? size.y : box.y, box.z);
        if (box != sized)
            SetDerived(collider.Size, sized);
    }

    public void EnsureRoot()
    {
        if (_rootRect == null || _chunkRoot == null)
        {
            bool mayAdd = IsMintedHere;
            var rect = _rootRect ?? Slot.GetComponent<RectTransform>() ?? (mayAdd ? Slot.AttachComponent<RectTransform>() : null);
            var chunkRoot = _chunkRoot ?? Slot.GetComponent<GraphicChunkRoot>() ?? (mayAdd ? Slot.AttachComponent<GraphicChunkRoot>() : null);
            if (rect == null || chunkRoot == null)
                return;
            _rootRect = rect;
            _chunkRoot = chunkRoot;
        }
        if (_rootChunk == null)
        {
            _rootChunk = new GraphicsChunk(this, _rootRect);
            _rootChunk.PrepareCompute();
            _rootChunk.SubmitChanges();
        }
        if (SizeCollider.Value)
            _uiCollider ??= Slot.GetComponent<BoxCollider>() ?? (IsMintedHere ? Slot.AttachComponent<BoxCollider>() : null);
    }

    public override void OnCommonUpdate()
    {
        base.OnCommonUpdate();
        Text.PollFonts(World);
        if (_placementDirty)
            ApplyPlacements();
        RequestPlayingFrames();
        bool dirty = _rootDirty || _fullDirty || _layoutDirty || _dirtyChunks.Count > 0 || _layoutDirtyChunks.Count > 0;
        if (!dirty)
            return;
        // One rebuild cycle at a time. A change arriving mid-cycle leaves its dirty flag set (we
        // don't clear it here), so the next OnCommonUpdate after the cycle finishes re-triggers.
        if (_updateRunning)
            return;
        StartRebuildCycle();
    }

    public override void OnDestroy()
    {
        foreach (var pointer in _pointers)
        {
            var context = new UIInteractionContext(this, UIInteractionSource.Unknown, pointer.Key, float2.Zero, float3.Zero, float3.Zero, float3.Zero, 0f);
            pointer.Value.Hovered?.NotifyHoverExit(in context);
            pointer.Value.Pressed?.NotifyRelease(in context);
        }
        _pointers.Clear();
        base.OnDestroy();
    }

    private readonly List<GraphicChunkRoot> _relayoutScratch = new();

    private void RelayoutDirtyChunks()
    {
        _relayoutScratch.Clear();
        _relayoutScratch.AddRange(_layoutDirtyChunks);
        _layoutDirtyChunks.Clear();
        foreach (var root in _relayoutScratch)
        {
            RelayoutChunkSubtree(root);
            // A scroll's content is its own chunk: re-fitted here, it is placed against its viewport again.
            ApplyScrollRects(root.Slot);
        }
        _relayoutScratch.Clear();
    }

    // Rects a RectLerp moved during the current layout pass.
    private readonly List<RectTransform> _blended = new();
    private readonly List<RectTransform> _blendScratch = new();

    internal void NoteBlended(RectTransform rect) => _blended.Add(rect);

    // A blend reads its target as placed at that moment, and a target placed later in the same pass (a sibling
    // after it, or one its layout reflowed afterwards) has moved since. Blend those again and place their
    // children in the new rect; a second round settles a blend toward a rect that is blended itself.
    private void SettleBlends()
    {
        for (int round = 0; round < 2 && _blended.Count > 0; round++)
        {
            _blendScratch.Clear();
            _blendScratch.AddRange(_blended);
            _blended.Clear();
            bool moved = false;
            foreach (var rect in _blendScratch)
            {
                if (rect.IsDestroyed || rect.Slot == null || rect.BlendedBy is not { TargetMoved: true })
                    continue;
                moved = true;
                rect.SetLocalComputeRect(rect.PlacedComputeRect);
                var slot = rect.Slot;
                if (SlotRunsLayout(slot))
                {
                    ApplyLayout(slot, rect);
                }
                else
                {
                    foreach (var c in slot.Children) ReanchorAndDescend(c, rect);
                    foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, rect);
                }
            }
            if (!moved)
                break;
        }
        _blended.Clear();
        _blendScratch.Clear();
    }

    // Text and wrapping layouts whose laid-out width changed during the current layout pass.
    private readonly List<RectTransform> _rewrapped = new();

    internal void NoteWidthChanged(RectTransform rect)
    {
        if ((rect.Slot?.GetComponent<Text>() is { } text && text.WordWrap.Value)
            || rect.Slot?.GetComponent<WrapLayout>() != null)
            _rewrapped.Add(rect);
    }

    // Marks each of them whose height was measured at another width; true when any was.
    private bool SettleWrapping()
    {
        bool any = false;
        for (int i = 0; i < _rewrapped.Count; i++)
        {
            var rect = _rewrapped[i];
            if (rect.IsDestroyed || rect.Slot == null)
                continue;
            float width = rect.PlacedComputeRect.width;
            if ((rect.Slot.GetComponent<Text>() is { } text && text.MeasuredAtOtherWidth(width))
                || (rect.Slot.GetComponent<WrapLayout>() is { } wrap && wrap.MeasuredAtOtherWidth(width)))
            {
                rect.MarkInvalidateVerticalLayout();
                any = true;
            }
        }
        _rewrapped.Clear();
        return any;
    }

    // MAIN: layout + prepare each dirty chunk (snapshot graphics, rasterize glyphs, queue geometry,
    // discover nested chunks), then hand the geometry build to a worker and submit back on main.
    private void StartRebuildCycle()
    {
        EnsureRoot();
        if (_rootRect == null || _rootChunk == null)
        {
            return;
        }

        // Only recompute the full layout when something layout-affecting actually changed.
        // Pure visual changes (hover/press tints, etc.) come in via MarkDirty(rect) without
        // setting _layoutDirty, so they re-mesh just their chunk and reuse the cached rects -
        // this is what stops every hover from re-laying-out the whole canvas. - xlinka
        if (_layoutDirty)
        {
            // MEASURE (bottom-up) before ARRANGE (top-down): cache each rect's metrics so a parent's
            // arrange sees its children's fully-propagated min/preferred/flexible. -xlinka
            _rewrapped.Clear();
            _blended.Clear();
            MeasureRects(Slot);
            ComputeRects(Slot, null);
            // Wrapped content was measured before this pass gave it its width; its height only holds for
            // the width it had. Measure the ones that moved and lay out once more, which settles them.
            if (SettleWrapping())
            {
                MeasureRects(Slot);
                ComputeRects(Slot, null);
                _rewrapped.Clear();
            }
            SettleBlends();
            ApplyScrollRects(Slot);
            UpdateCanvasBounds();
            _layoutDirty = false;
            _layoutDirtyChunks.Clear();
        }
        else if (_layoutDirtyChunks.Count > 0)
        {
            // Scoped: only the touched chunks' subtrees moved (e.g. a slider handle). Re-lay-out
            // those subtrees against their unchanged chunk-root rect; their chunk is already in
            // _dirtyChunks so the mesh re-renders too. No whole-canvas layout pass.
            _rewrapped.Clear();
            _blended.Clear();
            RelayoutDirtyChunks();
            _hitBoundsStale = true;
            // Moved wrapping content is measured at its new width and laid out again in this cycle.
            if (SettleWrapping())
            {
                RelayoutDirtyChunks();
                _rewrapped.Clear();
            }
            SettleBlends();
        }

        // Capture + clear the root/chunk dirt BEFORE the prepare pass (but AFTER the layout branch,
        // so rect-change dirt raised by this cycle's layout is still consumed now): anything raised
        // DURING prepare (chunk SetActive side effects, glyph re-drives) must survive to the next
        // cycle instead of being wiped with the pre-pass state. -xlinka
        bool fullDirty = _fullDirty;
        bool renderRoot = _fullDirty || _rootDirty;
        _cycleRenderRoot = renderRoot;
        _cycleDirtyChunks.Clear();
        foreach (var dirty in _dirtyChunks)
            _cycleDirtyChunks.Add(dirty);
        _rootDirty = false;
        _fullDirty = false;
        _dirtyChunks.Clear();

        // PREPARE PASS (main): clear meshes + queue graphics (snapshot + glyph raster on main),
        // discover nested chunks. The geometry build (ComputeGraphic) is queued, not run here.
        _computedChunks.Clear();
        if (renderRoot)
        {
            _seenChunkRoots.Clear();
            _chunkOrder.Clear();
            _rootChunk.PrepareCompute();
            RenderPartition(_rootChunk.ContentRenderData, Slot, null, null, null);

            // Prepare every nested chunk that's dirty/unbuilt. ComputeChunk can discover deeper
            // nested roots, so drain a worklist until stable. - xlinka
            _chunkScratch.Clear();
            _chunkScratchSet.Clear();
            foreach (var seen in _seenChunkRoots)
            {
                if (_chunkScratchSet.Add(seen))
                    _chunkScratch.Add(seen);
            }
            for (int i = 0; i < _chunkScratch.Count; i++)
            {
                var root = _chunkScratch[i];
                bool built = _chunkBuilt.TryGetValue(root, out var b) && b;
                // Re-mesh only chunks that are new, explicitly dirty, moved, or on a genuine full
                // refresh - NOT every chunk on any structural change. -xlinka
                if ((fullDirty || _cycleDirtyChunks.Contains(root) || !built || ChunkMoved(root)) && ComputeChunk(root))
                {
                    _computedChunks.Add(root);
                    if (root.Slot.GetComponent<RectTransform>() is { } crt)
                        _chunkMeshedRect[root] = crt.LocalComputeRect;
                }
                else
                    // Built + unchanged: we skip re-meshing it, but it may CONTAIN nested chunks (a panel chunk
                    // with its own per-row chunks). ComputeChunk would have discovered+registered those; since
                    // we skipped it, register them ourselves so they stay "seen" - otherwise CleanupChunks
                    // disables them and the panel's nested content vanishes when you toggle a control. -xlinka
                    RegisterNestedChunkRoots(root.Slot);
                foreach (var r in _seenChunkRoots)
                    if (_chunkScratchSet.Add(r)) _chunkScratch.Add(r);
            }

            // Re-enable every chunk visible this cycle. Persisted chunks that weren't recomputed
            // (built + unchanged) just toggle their slot back on here - instant, no re-tessellate;
            // recomputed ones get their fresh mesh at submit. This is what makes re-showing content
            // appear immediately instead of rebuilding from scratch.
            foreach (var seen in _seenChunkRoots)
                if (_chunkMap.TryGetValue(seen, out var chunk))
                    chunk.SetActive(true);
        }
        else
        {
            // Only nested chunks changed (e.g. the sparkline): re-mesh just those.
            foreach (var root in _cycleDirtyChunks)
            {
                if (_chunkMap.ContainsKey(root) && ComputeChunk(root))
                    _computedChunks.Add(root);
            }
        }

        _emitFailed = false;
        _updateRunning = true;

        // Emit (geometry build) runs on a background worker. Everything
        // ComputeGraphic touches is snapshotted on the main thread in the prepare pass above
        // (PrepareCompute/PreGraphicsCompute) - graphic values, shaped glyph lines, font metrics,
        // cached texture sizes - so the worker only writes managed mesh data and never calls into
        // Godot. (The earlier "random disappear" was Text reading the Godot font server off-main;
        // those reads now happen on main.) A change arriving mid-cycle is gated by _updateRunning.
        if (UseWorker)
        {
            Task.Run(() =>
            {
                try
                {
                    EmitCycle();
                }
                catch (Exception ex)
                {
                    _emitFailed = true;
                    Lumora.Core.Logging.Logger.Error($"Canvas graphics worker failed: {ex}");
                }
                finally
                {
                    World?.RunSynchronously(FinishRebuildCycle);
                }
            });
        }
        else
        {
            EmitCycle();
            FinishRebuildCycle();
        }
    }

    // Master switch for the off-main-thread emit. static readonly (not const) so both branches
    // compile without an unreachable warning when toggled.
    private static readonly bool UseWorker = true;

    // Per-surface unbounded ordering: each chunk renders every mesh surface as its own MeshInstance3D ordered by a
    // distinct full-range SortingOffset (= chunk tree index * stride + surface index) with uniform render_priority,
    // beating Godot's 256-level render_priority cap. DEFAULT OFF: it ghosts/double-renders during screen + modal
    // transitions (per-surface instances not torn down cleanly when a chunk is disabled/re-meshed - needs the
    // instance lifecycle tied to chunk visibility, debugged with the engine running). The banded path (off) is
    // stable. Only flip true for a focused render-test session, not for normal use. -xlinka
    public static bool UnboundedRenderOrder = false;

    // WORKER: drain each dirty chunk's emit queue into its (already-cleared) mesh. Reads only
    // snapshotted graphic state + stable LocalComputeRect; writes only managed PhosMesh data.
    private void EmitCycle()
    {
        if (IsDestroyed)
            return;
        // Exception isolation PER CHUNK: one poisoned chunk must not abort the rest of the batch
        // mid-emit. Any failure flags the cycle so FinishRebuildCycle retries instead of submitting
        // (and latching as built) half-built empty meshes. -xlinka
        if (_cycleRenderRoot)
        {
            try
            {
                _rootChunk?.ContentRenderData.EmitQueued();
            }
            catch (Exception ex)
            {
                _emitFailed = true;
                Lumora.Core.Logging.Logger.Error($"Canvas root chunk emit failed: {ex}");
            }
        }
        foreach (var root in _computedChunks)
        {
            if (!_chunkMap.TryGetValue(root, out var chunk))
                continue;
            try
            {
                chunk.ContentRenderData.EmitQueued();
            }
            catch (Exception ex)
            {
                _emitFailed = true;
                Lumora.Core.Logging.Logger.Error($"Canvas chunk emit failed ({root.Slot?.Name?.Value}): {ex}");
            }
        }
    }

    // MAIN: push the worker-built meshes + their materials to the renderers, then release the guard.
    private void FinishRebuildCycle()
    {
        if (!IsDestroyed)
        {
            if (_emitFailed)
            {
                // A failed emit means cleared/partial meshes: submitting would latch EMPTY chunks
                // as built and blank them until an unrelated dirty finally re-meshed them. The
                // renderers keep their last GOOD meshes as long as we don't submit - so re-flag
                // everything this cycle touched and retry on the next update. -xlinka
                foreach (var root in _computedChunks)
                    _dirtyChunks.Add(root);
                if (_cycleRenderRoot)
                    _rootDirty = true;
            }
            else
            {
                SubmitCycle();
            }
        }
        // A change that arrived during the cycle left its dirty flag set, so the next
        // OnCommonUpdate starts a fresh cycle.
        _updateRunning = false;
        _playingFps = MeasurePlayingFps();
        // The captured mesh just changed on the main thread - ask the host for one offscreen render. -xlinka
        _renderRequested = true;
    }

    // ANIMATED PICTURES ON A CAPTURED CANVAS
    //
    // A playing frame sheet changes its pixels on the GPU clock with no mesh change, so a host that captures the
    // canvas only when it changes (the dash, popped-out panels) froze it on whichever frame was up at the last
    // capture. While one is drawn, the canvas asks for a capture at twice its rate, never more than 30 a second;
    // a canvas drawn straight into the world renders every frame anyway and only sets a flag. -xlinka
    private const float MaxPlayingCaptures = 30f;

    private float MeasurePlayingFps()
    {
        float fps = _rootChunk?.ContentRenderData.PlayingFps ?? 0f;
        foreach (var pair in _chunkMap)
        {
            var chunk = pair.Value;
            if (chunk.ContentRenderData.PlayingFps > fps && !pair.Key.IsDestroyed && chunk.ChunkSlot is { IsDestroyed: false } slot && slot.IsActive)
                fps = chunk.ContentRenderData.PlayingFps;
        }
        return fps;
    }

    private void RequestPlayingFrames()
    {
        if (_playingFps <= 0f || _updateRunning || World == null)
            return;
        double now = World.Time.TotalTime;
        if (now - _playedAt < 1.0 / System.Math.Min(_playingFps * 2f, MaxPlayingCaptures))
            return;
        _playedAt = now;
        _renderRequested = true;
    }

    private void SubmitCycle()
    {
        if (_rootChunk == null)
            return;

        int baseOrder = Overlay.Value && !DrawsOverlay ? 0 : SortingOrder.Value;

        // On a render-root cycle the chunk set / tree order can change, so (re)assign every seen chunk a
        // tree-order SortingOrder: root lowest, then nested chunks in discovery (hierarchy) order. SortingOrder
        // is the INNER transparent-sort key (Godot sorting_offset) under render_priority, so later-in-tree
        // chunks tie-break ON TOP of earlier same-band ones. Push it straight to each renderer here so unchanged
        // chunks reorder WITHOUT a re-mesh; recomputed/new chunks pick the same value up via SubmitChanges.
        // On a scoped cycle we don't rebuild _chunkOrder - each chunk keeps the OrderIndex it was last given,
        // which is still correct because the tree didn't change structurally. -xlinka
        if (_cycleRenderRoot)
        {
            _rootChunk.OrderIndex = baseOrder;
            for (int i = 0; i < _chunkOrder.Count; i++)
            {
                if (_chunkMap.TryGetValue(_chunkOrder[i], out var ch))
                {
                    ch.OrderIndex = baseOrder + 1 + i;
                    ch.ApplyOrderToRenderer();
                }
            }
            ApplyCullMargin(_rootChunk, ChunkCullSlack);
            _rootChunk.SubmitChanges(_rootChunk.OrderIndex);
        }

        foreach (var root in _computedChunks)
        {
            if (_chunkMap.TryGetValue(root, out var chunk))
            {
                // Scroll-participating chunks (the content root and every row chunk nested under one) displace
                // their verts in the vertex shader, so their baked AABB no longer bounds the visible pixels -
                // size the cull margin BEFORE submit (the per-surface instances read it as they're built) or
                // Godot frustum-culls them at glancing angles / up close ("panel clips when I get close"). -xlinka
                bool scrolled = ScrollRenderOffset &&
                    (root.ScrollContent || FindScrollContentAncestor(root.Slot) != null);
                ApplyCullMargin(chunk, ChunkCullMargin(root));

                chunk.SubmitChanges(chunk.OrderIndex);
                // Render-offset content: its materials were just (re)cloned by submit, so re-pin the clip
                // offset onto them for the current scroll position. persist=true so the materials' own update
                // (queued by the re-clone) doesn't re-push the baked 0 and snap the content back to the top.
                // Applies to the scroll content root AND every chunk nested under one: inspector/session rows
                // are their own chunks (per-row re-mesh perf), and a chunk that never gets the offset renders
                // frozen while the (empty) content chunk scrolls - THE "value moves, pixels don't" bug. The
                // offset is cumulative so a scroll nested inside a scroll (code editor in a panel) rides both. -xlinka
                if (scrolled)
                    chunk.SetClipOffset(CumulativeScrollOffset(root), persist: true);
                _chunkBuilt[root] = true;
            }
        }

        if (_cycleRenderRoot)
        {
            CleanupChunks();
            ApplyPlacements();
        }
        else
            DisposeDeadChunks();
    }

    private readonly List<GraphicsChunk> _sheets = new();

    private void ApplyPlacements()
    {
        _placementDirty = false;
        bool moved = false;
        if (_rootChunk != null)
            moved |= _rootChunk.SetPlacement(PlacementOf(Slot, 0, out float rootScale, out _), rootScale);
        _sheets.Clear();
        foreach (var pair in _chunkMap)
        {
            if (pair.Key.IsDestroyed || pair.Key.Slot == null)
                continue;
            var chunk = pair.Value;
            moved |= chunk.SetPlacement(PlacementOf(pair.Key.Slot, pair.Key.OverlayLevel, out float scale, out bool sheet), scale);
            // A layer put on or taken off a chunk root changes what its first surface is; that takes a re-mesh.
            if (sheet != chunk.IsSheet)
                _dirtyChunks.Add(pair.Key);
            if (chunk.IsSheet && chunk.ChunkSlot != null && !chunk.ChunkSlot.IsDestroyed && chunk.ChunkSlot.ActiveSelf.Value)
                _sheets.Add(chunk);
        }
        RankWalls();
        if (moved)
            _renderRequested = true;
    }

    // Sheet walls draw before everything else of every sheet, the highest lift first, so each lays its depth
    // where nothing of a lower sheet has drawn yet and the lower sheet is cut away under it, backing and all.
    // At equal lift tree order holds. A raise re-ranks the few sheets there are and touches only a wall whose
    // place changed: a priority and a material clone, no mesh.
    //
    // The band holds one slot a visible sheet, so it widens when more windows are up than the minimum and gives
    // the root chunk its slots back after; the root re-meshes into its new band only when the width moves. In
    // the per-surface order the walls take the last keys of the root chunk's stride instead, so they still go
    // down after the root and before every sheet. -xlinka
    public int WallSlots { get; private set; } = GraphicsChunk.WallSlotsMin;

    private void RankWalls()
    {
        int slots = System.Math.Clamp(_sheets.Count, GraphicsChunk.WallSlotsMin, GraphicsChunk.WallSlotsMax);
        if (slots != WallSlots)
        {
            WallSlots = slots;
            MarkDirty();
        }
        if (_sheets.Count == 0)
            return;
        _sheets.Sort(NearestSheetFirst);
        int rootOrder = _rootChunk?.MeshRenderer?.SortingOrder.Value ?? 0;
        float leadBase = (rootOrder + 1) * MeshRenderer.PerSurfaceSortStride - _sheets.Count;
        for (int i = 0; i < _sheets.Count; i++)
            _sheets[i].SetWallPriority(System.Math.Min(-1, -slots + i), leadBase + i);
    }

    private static readonly Comparison<GraphicsChunk> NearestSheetFirst = (a, b) =>
    {
        int lift = b.Placement.z.CompareTo(a.Placement.z);
        return lift != 0 ? lift : a.OrderIndex.CompareTo(b.OrderIndex);
    };

    // Every enabled StackLayer from the chunk's root up to the canvas, composed inner to outer: each scales
    // what is under it about its pivot, then slides it. A chunk no layer carries is flat, unless it is an
    // overlay level, which takes OverlayLift. sheet: the root itself carries one.
    private float3 PlacementOf(Slot? slot, int overlayLevel, out float scale, out bool sheet)
    {
        float2 offset = float2.Zero;
        float lift = 0f;
        bool layered = false;
        scale = 1f;
        sheet = false;
        for (var s = slot; s != null; s = s.Parent)
        {
            var layer = s.GetComponent<StackLayer>();
            if (layer != null && layer.Enabled.Value)
            {
                float k = layer.Scale.Value;
                var pivot = layer.Pivot.Value;
                offset = layer.Offset.Value + pivot + (offset - pivot) * k;
                scale *= k;
                lift += layer.Lift.Value;
                layered = true;
                sheet |= ReferenceEquals(s, slot);
            }
            if (ReferenceEquals(s, Slot))
                break;
        }
        if (!layered && overlayLevel > 0)
            lift = OverlayLift;
        return new float3(offset.x, offset.y, lift);
    }

    // Chunk slots are filed FLAT under the canvas slot (GraphicsChunk.EnsureSlot), not under the element
    // that owns the chunk, so destroying a row slot does not take its mesh with it. Only CleanupChunks
    // frees a dead chunk, and that runs on ROOT passes alone; a structural rebuild inside a built nested
    // chunk (the inspector tree re-listing its rows, all their own chunks) takes the scoped path and never
    // gets one. The dead row meshes then keep rendering under the new rows until some unrelated root pass
    // sweeps them: ghost rows, most visibly a stale highlighted root row stacked above the real one. Sweep
    // dead chunks on EVERY cycle; the not-seen -> deactivate half of CleanupChunks stays root-only because
    // a scoped pass has no complete seen-set to judge liveness by. -xlinka
    private void DisposeDeadChunks()
    {
        _chunkScratch.Clear();
        foreach (var pair in _chunkMap)
        {
            var root = pair.Key;
            if (root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed)
                _chunkScratch.Add(root);
        }
        foreach (var root in _chunkScratch)
        {
            _chunkMap[root].Dispose();
            _chunkMap.Remove(root);
            _chunkBuilt.Remove(root);
            _chunkMeshedRect.Remove(root);
            _offsetBakedClipChunks.Remove(root);
        }
    }

    // Nearest ancestor GraphicChunkRoot flagged ScrollContent, strictly ABOVE this slot (stops at the canvas
    // root). Rows inside a scroll content are their own chunks; this is how they're recognized as scrolled. -xlinka
    private GraphicChunkRoot? FindScrollContentAncestor(Slot start)
    {
        for (var s = start?.Parent; s != null && !ReferenceEquals(s, Slot); s = s.Parent)
        {
            var cr = s.GetComponent<GraphicChunkRoot>();
            if (cr != null && cr.ScrollContent)
                return cr;
        }
        return null;
    }

    // How far, in canvas units, this chunk's pixels can end up from the geometry it was baked at. Every
    // ScrollContent ancestor can slide it by at most that ancestor's own content size (you cannot scroll a
    // list further than the list is long), and nested scrolls compose, so the sum is a true upper bound.
    // Anything not under a scroll just gets the slack. -xlinka
    private float ChunkCullMargin(GraphicChunkRoot root)
    {
        float margin = ChunkCullSlack;
        if (!ScrollRenderOffset || root == null)
            return margin;

        var content = root.ScrollContent ? root : FindScrollContentAncestor(root.Slot);
        while (content?.Slot != null)
        {
            float reach = 0f;
            var rect = content.Slot.GetComponent<RectTransform>();
            if (rect != null)
            {
                var r = rect.LocalComputeRect;
                reach = MathF.Max(MathF.Abs(r.width), MathF.Abs(r.height));
            }
            // The content rect is the bound in theory, the live offset is the bound in fact, and the two
            // disagree whenever the rect is read before the fitter has sized it (a fresh RectTransform is
            // 100x100, and a viewport-sized rect is the height of the WINDOW, not of the list). Trusting
            // the rect alone gives a margin smaller than the scroll, which cuts the list off part way down
            // - exactly the symptom, just further along. Take whichever is bigger. -xlinka
            var off = content.RenderOffset;
            reach = MathF.Max(reach, MathF.Max(MathF.Abs(off.x), MathF.Abs(off.y)));
            margin += reach;
            content = FindScrollContentAncestor(content.Slot);
        }
        return margin;
    }

    // raiseOnly is for the live scroll path: it walks every chunk under the content each frame, and the
    // margin only ever changes when the content RESIZES, so comparing first keeps the hot path free of
    // change-queue traffic.
    private static void ApplyCullMargin(GraphicsChunk chunk, float margin, bool raiseOnly = false)
    {
        var renderer = chunk?.MeshRenderer;
        if (renderer == null || renderer.IsDestroyed)
            return;
        if (raiseOnly ? margin <= renderer.ExtraCullMargin : margin == renderer.ExtraCullMargin)
            return;
        renderer.ExtraCullMargin = margin;
        renderer.MarkChangeDirty();
    }

    // Total scroll offset a chunk should render at: its own RenderOffset (if it IS scroll content) plus every
    // ScrollContent ancestor's offset. Cumulative so a scroll nested inside a scroll composes linearly. -xlinka
    private float2 CumulativeScrollOffset(GraphicChunkRoot root)
    {
        float2 own = root.ScrollContent ? root.RenderOffset : float2.Zero;
        float2 total = own;
        for (var s = root.Slot?.Parent; s != null && !ReferenceEquals(s, Slot); s = s.Parent)
            total += SlotScrollStep(s);
        return total;
    }

    // One slot's contribution to the render displacement: a ScrollRect's content chunk is slid by its
    // RenderOffset (the shader's clip_offset moves the verts AND the clip test together), so this slot and
    // everything under it DRAWS that far from its layout rect. Zero for every non-scroll slot. -xlinka
    private static float2 SlotScrollStep(Slot? slot)
    {
        if (!ScrollRenderOffset || slot == null)
            return float2.Zero;
        var cr = slot.GetComponent<GraphicChunkRoot>();
        return cr != null && cr.ScrollContent ? cr.RenderOffset : float2.Zero;
    }

    // What a hit sees of one slot: its scroll, plus the slide of a StackLayer on it. The layer's slide is a
    // chunk transform on the render side, so it stays out of clip_offset; a pointer still has to undo it.
    private static float2 SlotHitStep(Slot? slot)
    {
        if (slot == null)
            return float2.Zero;
        var step = SlotScrollStep(slot);
        var layer = slot.GetComponent<StackLayer>();
        if (layer != null && layer.Enabled.Value)
            step += layer.Offset.Value;
        return step;
    }

    // gap between a slot's LAYOUT rect and where it actually renders: every ScrollContent chunk root at
    // or above it, summed (nested scrolls compose linearly).
    // THE single source for that number. Rendering (CumulativeScrollOffset -> clip_offset) and every hit path
    // (ScanHitSlot / FindDeepestRectAt / CanvasPointToLocal) go through this one walk, so the pixel you see and
    // the thing you hit can't drift apart - which is exactly what they did while hit-testing read raw layout
    // rects and rendering read the offset. -xlinka
    public float2 ScrollOffsetOf(Slot? slot)
    {
        float2 total = float2.Zero;
        for (var s = slot; s != null && !ReferenceEquals(s, Slot); s = s.Parent)
            total += SlotHitStep(s);
        return total;
    }

    // maps a canvas-space point (what UIInteractionContext.LocalPoint carries) into a slot's own layout
    // space by undoing the scroll displacement it renders at. anything comparing a pointer point to a
    // RectTransform rect - value-from-point, cell-from-point, point-in-panel - must go through this, or
    // it reads the rect as it sat at scroll 0.
    public float2 CanvasPointToLocal(Slot? slot, in float2 canvasPoint) => canvasPoint - ScrollOffsetOf(slot);

    private bool IsSlotDescendantOf(Slot slot, Slot ancestor)
    {
        for (var s = slot?.Parent; s != null && !ReferenceEquals(s, Slot); s = s.Parent)
        {
            if (ReferenceEquals(s, ancestor))
                return true;
        }
        return false;
    }

    // Tessellate a nested chunk's mesh without submitting it. Returns true if computed.
    private bool ComputeChunk(GraphicChunkRoot root)
    {
        if (!_chunkMap.TryGetValue(root, out var chunk))
            return false;
        // The level is a plain property that can be set after the chunk was first registered (a replicated
        // panel whose owner sets it on start), so pick it up on every re-mesh rather than only at creation.
        chunk.OverlayLevel = root.OverlayLevel;

        // This chunk's mesh rides a scroll offset - it IS scroll content, or it's a row/box chunk nested under
        // some (those scroll too). Drives all three clip decisions below: no clip elision (an item fully in
        // view at bake scrolls out later, and an elided clip would let it spill), full geometry bake against
        // the fixed window, and the window/riding split. -xlinka
        bool ridesScroll = ScrollRenderOffset && (root.ScrollContent || FindScrollContentAncestor(root.Slot) != null);
        var clips = ComputeInheritedClips(root, ridesScroll);
        var stencil = ComputeInheritedStencil(root.Slot);

        // Only the genuinely nested case (a scroll inside a scroll) bakes a live offset into the window; that
        // window needs re-baking when the ancestor moves, and ApplyScrollOffset uses this set to find it.
        if (clips.WindowRidesScroll)
            _offsetBakedClipChunks.Add(root);
        else
            _offsetBakedClipChunks.Remove(root);

        // Render-offset scrolling: the content is moved by the clip_offset uniform IN THE SHADER (it offsets
        // the vertex and clips against the same value, so position and clip can never desync), NOT by the
        // chunk transform. So the chunk slot stays at the origin; the scroll offset rides on the material's
        // clip_offset via SetClipOffset (after submit, and on each live scroll). The clip rect stays fixed in
        // canvas space, so it isn't in the material key and doesn't re-clone every step. -xlinka
        chunk.SetPlacement(PlacementOf(root.Slot, root.OverlayLevel, out float placedScale, out bool sheet), placedScale);
        chunk.IsSheet = sheet;
        chunk.SetComputeOffset(float2.Zero);

        chunk.PrepareCompute();
        _chunkRidesScroll = ridesScroll;
        // Scroll content must bake its FULL geometry (nothing culled or trimmed to the FIXED viewport window),
        // or items below the fold are never in the mesh and scrolling reveals empty space. The shader clips
        // those at render via the material rect + clip_offset. A window that RIDES with this chunk is the
        // opposite case and IS trimmed at bake - see RenderData.GeometryClipRect. -xlinka
        chunk.ContentRenderData.SuppressGeometryClip = ridesScroll;
        _partitionFoundChunk = false;
        RenderPartition(chunk.ContentRenderData, root.Slot, clips.Window, clips.Riding, root, stencil);
        chunk.ContainsChunks = _partitionFoundChunk;
        _chunkRidesScroll = false;
        return true;
    }

    // Scroll a ScrollRect's content by MOVING its chunk, not re-tessellating it - the whole point of the
    // perf fix. Sets the chunk slot's LocalPosition (the SlotHook flushes it to the
    // Node3D) and stores the offset so a later structural rebuild re-applies it. The mesh is untouched; the
    // ancestor stencil still clips the moved content. Returns false if the chunk isn't built yet, so the
    // caller can fall back to a full rebuild. -xlinka
    public bool ApplyScrollOffset(GraphicChunkRoot content, float2 pixelOffset)
    {
        if (content == null || !ScrollRenderOffset)
            return false;
        content.RenderOffset = pixelOffset;
        if (_chunkMap.TryGetValue(content, out var chunk) && chunk.ChunkSlot != null && !chunk.ChunkSlot.IsDestroyed)
        {
            // Move + clip the content in one shot via the shader's clip_offset uniform (no re-mesh, no chunk
            // transform, so position and clip stay locked together). persist=false: write the shader param
            // directly, no per-material asset rebuild - this is the hot path hit every scroll frame. -xlinka
            chunk.SetClipOffset(CumulativeScrollOffset(content), persist: false);

            // The cull box has to cover wherever the offset can put the pixels. It is normally the content
            // SIZE, which does not change while you scroll, so this writes nothing on a plain scroll and
            // costs the comparison; the live-offset floor inside it only takes over when the content rect
            // has not been sized yet, which is the one case that must be chased per frame.
            float cullMargin = ChunkCullMargin(content);
            ApplyCullMargin(chunk, cullMargin, raiseOnly: true);

            // Rows built inside the content are their OWN chunks (per-row re-mesh perf) with their own material
            // clones - the content chunk's uniform doesn't reach them. Push the offset to every built chunk
            // nested under the scrolled content or they render frozen while the (empty) content chunk moves.
            // Cumulative per chunk so an inner scroll (code editor in a panel) keeps its own offset on top. -xlinka
            foreach (var kv in _chunkMap)
            {
                if (ReferenceEquals(kv.Key, content))
                    continue;
                var nested = kv.Value;
                if (nested.ChunkSlot == null || nested.ChunkSlot.IsDestroyed)
                    continue;
                if (!IsSlotDescendantOf(kv.Key.Slot, content.Slot))
                    continue;
                nested.SetClipOffset(CumulativeScrollOffset(kv.Key), persist: false);
                ApplyCullMargin(nested, cullMargin, raiseOnly: true);
                // A chunk under a SECOND scroll carries an ancestor mask's displacement baked into its material
                // window (a fixed canvas-space rect can't ride a live offset), and this move just invalidated
                // that number - the verts follow clip_offset but the baked window doesn't. Re-mesh it. Only a
                // scroll inside a scroll ever lands in this set, so the normal list scroll keeps the
                // uniform-only path; the price of the nested case is a re-tessellation per scroll frame. -xlinka
                if (_offsetBakedClipChunks.Contains(kv.Key))
                    _dirtyChunks.Add(kv.Key);
            }

            _renderRequested = true;   // repaint the offscreen dashboard capture this frame
            return true;
        }
        return false;
    }

    // Walk a chunk's subtree and register the FIRST GraphicChunkRoot on each branch into _seenChunkRoots,
    // WITHOUT re-meshing anything. Used when a render-root cycle skips recomputing a built+unchanged chunk:
    // its nested chunks still have to count as "seen" this cycle or CleanupChunks disables them. Mirrors
    // RenderPartition's active-self skipping; stops at each chunk boundary (the prepare loop then picks those
    // up and registers THEIR nested chunks in turn). -xlinka
    private void RegisterNestedChunkRoots(Slot slot)
    {
        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
            RegisterNestedChunkRootsRecursive(children[i]);
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
            RegisterNestedChunkRootsRecursive(localChildren[i]);
    }

    private void RegisterNestedChunkRootsRecursive(Slot slot)
    {
        if (!slot.ActiveSelf.Value)
            return;
        var cr = slot.GetComponent<GraphicChunkRoot>();
        if (cr != null)
        {
            EnsureChunk(cr);
            if (_seenChunkRoots.Add(cr)) _chunkOrder.Add(cr);
            return; // chunk boundary
        }
        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
            RegisterNestedChunkRootsRecursive(children[i]);
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
            RegisterNestedChunkRootsRecursive(localChildren[i]);
    }

    private void EnsureChunk(GraphicChunkRoot root)
    {
        if (_chunkMap.ContainsKey(root))
            return;
        var rect = root.Slot.GetComponent<RectTransform>();
        if (rect == null)
            return;
        var chunk = new GraphicsChunk(this, rect);
        chunk.RenderOnTop = true;
        chunk.OverlayLevel = root.OverlayLevel;
        _chunkMap[root] = chunk;
        _chunkBuilt[root] = false;
    }

    // Reconcile chunks not seen this cycle. A chunk whose root slot is gone is truly removed -> dispose.
    // A chunk that's just hidden (subtree inactive) is DISABLED but kept, so re-showing it is an
    // instant re-enable, not a dispose + rebuild. - xlinka
    private void CleanupChunks()
    {
        _chunkScratch.Clear();
        foreach (var pair in _chunkMap)
        {
            if (!_seenChunkRoots.Contains(pair.Key))
                _chunkScratch.Add(pair.Key);
        }
        foreach (var root in _chunkScratch)
        {
            var chunk = _chunkMap[root];
            // Gone, or moved under another canvas: either way this canvas has nothing left to draw for it.
            bool removed = root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed || !Holds(root.Slot);
            if (removed)
            {
                chunk.Dispose();
                _chunkMap.Remove(root);
                _chunkBuilt.Remove(root);
                _chunkMeshedRect.Remove(root);
                _offsetBakedClipChunks.Remove(root);
            }
            else
            {
                chunk.SetActive(false);
            }
        }
    }

    // Nearest GraphicChunkRoot strictly below the canvas slot, or null if the rect belongs to the
    // root chunk. The canvas slot's own GraphicChunkRoot is the root marker, not a nested chunk. - xlinka
    private GraphicChunkRoot? FindChunkRoot(Slot? slot)
    {
        while (slot != null && !ReferenceEquals(slot, Slot))
        {
            var cr = slot.GetComponent<GraphicChunkRoot>();
            if (cr != null)
                return cr;
            slot = slot.Parent;
        }
        return null;
    }

    // Clip a nested chunk inherits from masks on its ancestors (above the chunk root), split by whether each
    // mask MOVES WITH the chunk's geometry.
    //
    // The shader displaces a vert by clip_offset and tests the DISPLACED position against the material rect, so
    // a material window is only right when it is a FIXED canvas-space window - true for a mask sitting above
    // every scroll the chunk rides, false for one that rides a scroll itself (its pixels move, the baked rect
    // doesn't, and the content gets sliced by a window that isn't where the mask is any more). A mask locked to
    // the chunk (nothing scrolls between them) keeps a fixed relationship to the verts, so it comes back as
    // Riding and is applied as a bake-time geometry trim instead - exact at any scroll position.
    //
    // A mask that rides a DIFFERENT amount than the chunk (a scroll inside a scroll) fits neither: it goes in
    // the window with its CURRENT displacement folded in, which is exact as baked and drifts if that ancestor
    // scrolls without a re-mesh. WindowRidesScroll flags those chunks so ApplyScrollOffset can re-bake them.
    //
    // split=false (a chunk that rides nothing) keeps the old single-window behavior verbatim: no scroll roots
    // exist above it, so every offset here is zero anyway. -xlinka
    private InheritedClips ComputeInheritedClips(GraphicChunkRoot root, bool split)
    {
        Rect? window = null;
        Rect? riding = null;
        bool windowRides = false;

        // Total displacement of this chunk's verts, and how many scrolls it comes from.
        float2 total = float2.Zero;
        int totalScrolls = 0;
        for (var s = root.Slot; s != null; s = s.Parent)
        {
            var cr = ReferenceEquals(s, root.Slot) ? root : s.GetComponent<GraphicChunkRoot>();
            if (cr != null && cr.ScrollContent)
            {
                total += cr.RenderOffset;
                totalScrolls++;
            }
            if (ReferenceEquals(s, Slot))
                break;
        }

        // Walking up, `crossed` is the part of that displacement the mask does NOT share, so the mask's own
        // displacement is (total - crossed) - exactly how far its window has to move to sit on its pixels.
        float2 crossed = float2.Zero;
        int crossedScrolls = 0;
        for (var s = root.Slot; s != null; s = s.Parent)
        {
            var mask = ReferenceEquals(s, root.Slot) ? null : s.GetComponent<Mask>();
            var rect = s.GetComponent<RectTransform>();
            if (mask != null && mask.Enabled.Value && rect != null)
            {
                if (split && crossedScrolls == 0)
                {
                    // Locked to the chunk: nothing scrolls between them, so their relationship is fixed and
                    // the window can be baked into the geometry.
                    var maskRect = rect.LocalComputeRect;
                    riding = riding.HasValue ? riding.Value.Intersection(maskRect) : maskRect;
                }
                else
                {
                    var maskRect = Translate(rect.LocalComputeRect, total - crossed);
                    window = window.HasValue ? window.Value.Intersection(maskRect) : maskRect;
                    // Structural, not value-based: a nested scroll parked at 0 right now still moves later,
                    // and a window baked as if it were static would be wrong the moment it does. -xlinka
                    windowRides |= totalScrolls - crossedScrolls > 0;
                }
            }
            var chunkRoot = ReferenceEquals(s, root.Slot) ? root : s.GetComponent<GraphicChunkRoot>();
            if (chunkRoot != null && chunkRoot.ScrollContent)
            {
                crossed += chunkRoot.RenderOffset;
                crossedScrolls++;
            }
            if (ReferenceEquals(s, Slot))
                break;
        }
        return new InheritedClips(window, riding, windowRides);
    }

    private static Rect Translate(in Rect rect, in float2 offset)
        => new Rect(rect.x + offset.x, rect.y + offset.y, rect.width, rect.height);

    private readonly struct InheritedClips
    {
        // Fixed canvas-space window, carried on the material and tested against the scroll-displaced position.
        public readonly Rect? Window;
        // Window that moves with the chunk's geometry - applied as a geometry trim at bake, never on a material.
        public readonly Rect? Riding;
        // Window has a live scroll offset baked into it (scroll inside a scroll): re-bake when it moves.
        public readonly bool WindowRidesScroll;

        public InheritedClips(Rect? window, Rect? riding, bool windowRidesScroll)
        {
            Window = window;
            Riding = riding;
            WindowRidesScroll = windowRidesScroll;
        }
    }

    // Stencil role a nested chunk inherits from a stencil-enabled Mask on its ancestors (above the chunk
    // root). Mirror of ComputeInheritedClips - without this, a shaped mask over a separately-chunked subtree
    // (e.g. a scroll region or any GraphicChunkRoot) would silently fall back to the rect-AABB clip instead
    // of the stencil shape. Single-depth, so any ancestor stencil mask => Test (the writer lives in an
    // ancestor chunk, which draws first via the root/tree-order priority, so the stencil is set in time). -xlinka
    private StencilRole ComputeInheritedStencil(Slot start)
    {
        if (!StencilMaskingEnabled)
            return StencilRole.None;
        for (var s = start.Parent; s != null; s = s.Parent)
        {
            var mask = s.GetComponent<Mask>();
            if (mask != null && mask.Enabled.Value && mask.StencilMasking.Value && s.GetComponent<RectTransform>() != null)
            {
                return StencilRole.Test;
            }
            if (ReferenceEquals(s, Slot))
                break;
        }
        return StencilRole.None;
    }

    // bottom-up: anchor-rect every descendant, then apply this layout, which propagates
    // overrides into descendant subtrees via ReflowAfterParentChanged. - xlinka
    // MEASURE pass (bottom-up, post-order): cache each rect's LayoutMetrics so the top-down arrange sees a
    // child's fully-propagated min/preferred/flexible - flexibility now survives nested containers instead
    // of collapsing to 0. Gated by the same LayoutSubtreeDirty skip as ComputeRects, so clean subtrees keep
    // their cached metrics. Runs BEFORE ComputeRects, which is what clears LayoutSubtreeDirty. -xlinka
    private void MeasureRects(Slot slot)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return;
        }

        var rect = slot.GetComponent<RectTransform>();
        // A clean, already-measured subtree keeps its cache (mirrors ComputeRects' cached-layout skip).
        if (rect != null && rect.MetricsValid && !rect.LayoutSubtreeDirty)
        {
            return;
        }

        // Children first, so a container measures over already-cached descendants.
        foreach (var child in slot.Children)
        {
            MeasureRects(child);
        }
        foreach (var child in slot.LocalChildren)
        {
            MeasureRects(child);
        }

        if (rect != null)
        {
            rect.SetRegisteredCanvas(this);
            // GetMetrics runs each ILayoutElement's EnsureValidMetrics, which now reads the children's
            // cached measured metrics (available post-order) - no live recursion, no flexible decay.
            // PER-AXIS: re-measure only the axis whose cached metric is stale; reuse the clean one. A change
            // that couldn't be narrowed to one axis sets BOTH dirty bits (the common case), so both re-measure
            // and the output is identical to before. The win is a provably single-axis change skipping the
            // other axis's GetMetrics. -xlinka
            if (rect.MetricsDirty(LayoutDirection.Horizontal))
                rect.SetMeasuredMetrics(LayoutDirection.Horizontal, LayoutSizing.GetMetrics(rect, LayoutDirection.Horizontal));
            if (rect.MetricsDirty(LayoutDirection.Vertical))
                rect.SetMeasuredMetrics(LayoutDirection.Vertical, LayoutSizing.GetMetrics(rect, LayoutDirection.Vertical));
        }
    }

    private void ComputeRects(Slot slot, RectTransform? parent)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return;
        }

        var rect = slot.GetComponent<RectTransform>();
        var nextParent = parent;

        if (rect != null)
        {
            rect.SetRegisteredCanvas(this);
            rect.SetRectParent(parent);
            if (parent != null)
            {
                parent.AddRectChild(rect);
            }

            var computed = ComputeRect(rect, parent);

            // CACHED LAYOUT: skip a clean subtree whose rect didn't change. Safe ONLY under a non-layout
            // parent - a LayoutController parent can reposition this rect from a sibling's change, so those
            // always recompute. A free-anchored rect's position depends solely on its anchors + the parent
            // rect, so if it's clean (nothing changed in it or below) and its recomputed rect matches the
            // cached one, the entire subtree is unchanged and its cached rects stay valid. This is what stops
            // a localized change (or a tab switch) from re-laying-out the whole canvas. -xlinka
            bool parentRunsLayout = parent != null && SlotRunsLayout(parent.Slot);
            if (!parentRunsLayout && !rect.LayoutSubtreeDirty && RectUnchanged(computed, rect.PlacedComputeRect))
            {
                return;
            }

            rect.ClearRectChildren();
            rect.SetLocalComputeRect(computed);
            rect.ClearLayoutDirty();
            nextParent = rect;
        }

        foreach (var child in slot.Children)
        {
            ComputeRects(child, nextParent);
        }
        foreach (var child in slot.LocalChildren)
        {
            ComputeRects(child, nextParent);
        }

        if (rect != null)
        {
            ApplyContentSizeFit(slot, rect);
            ApplyAspectRatio(slot, rect);
            ApplyLayout(slot, rect);
        }
    }

    // ContentSizeFitter / self-size: now that this container's children are registered, resize the
    // container's own rect to its content metric (about its pivot), then re-anchor the children against
    // the new size so a shrink-wrap panel/list/button fits its content instead of its anchor box. This
    // is the bottom-up sizing the layout system was missing - it only fires when a ContentSizeFitter is
    // present + enabled, so fixed-anchor layouts are unaffected. -xlinka
    private void ApplyContentSizeFit(Slot slot, RectTransform rect)
    {
        if (!FitToContent(slot, rect))
            return;
        foreach (var c in slot.Children) ReanchorAndDescend(c, rect);
        foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, rect);
    }

    // Sizes the rect to its content metric about its pivot; true when that changed it.
    private static bool FitToContent(Slot slot, RectTransform rect)
    {
        var fitter = slot.GetComponent<Helio.UI.Layout.ContentSizeFitter>();
        if (fitter == null || !fitter.Enabled.Value)
            return false;

        var hFit = fitter.HorizontalFit.Value;
        var vFit = fitter.VerticalFit.Value;
        if (hFit == SizeFit.Disabled && vFit == SizeFit.Disabled)
            return false;

        var r = rect.PlacedComputeRect;
        var pivot = rect.Pivot.Value;
        float w = r.width;
        float h = r.height;

        if (hFit != SizeFit.Disabled)
        {
            var m = LayoutSizing.Measured(rect, LayoutDirection.Horizontal);
            w = hFit == SizeFit.MinSize ? m.Min : m.Preferred;
        }
        if (vFit != SizeFit.Disabled)
        {
            var m = LayoutSizing.Measured(rect, LayoutDirection.Vertical);
            h = vFit == SizeFit.MinSize ? m.Min : m.Preferred;
        }

        if (w == r.width && h == r.height)
            return false;

        // Keep the pivot point fixed while the size changes.
        float pivotX = r.xMin + pivot.x * r.width;
        float pivotY = r.yMin + pivot.y * r.height;
        rect.SetLocalComputeRect(new Rect(pivotX - pivot.x * w, pivotY - pivot.y * h, w, h));
        return true;
    }

    // Aspect-ratio lock: after content-fit, adjust this rect to a target width/height
    // ratio (about its pivot) and reflow children. Mirrors ApplyContentSizeFit's path.
    private void ApplyAspectRatio(Slot slot, RectTransform rect)
    {
        var constraint = slot.GetComponent<Helio.UI.Layout.AspectRatioConstraint>();
        if (constraint == null || !constraint.Enabled.Value)
            return;

        float aspect = constraint.AspectRatio.Value;
        if (aspect <= 0f)
            return;

        var r = rect.PlacedComputeRect;
        float w = r.width;
        float h = r.height;

        switch (constraint.Mode.Value)
        {
            case Helio.UI.Layout.AspectRatioConstraint.AspectMode.WidthControlsHeight:
                h = w / aspect;
                break;
            case Helio.UI.Layout.AspectRatioConstraint.AspectMode.HeightControlsWidth:
                w = h * aspect;
                break;
            case Helio.UI.Layout.AspectRatioConstraint.AspectMode.FitInParent:
                if (w / aspect > h) w = h * aspect; else h = w / aspect;
                break;
            case Helio.UI.Layout.AspectRatioConstraint.AspectMode.EnvelopeParent:
                if (w / aspect < h) w = h * aspect; else h = w / aspect;
                break;
        }

        if (w == r.width && h == r.height)
            return;

        var pivot = rect.Pivot.Value;
        float pivotX = r.xMin + pivot.x * r.width;
        float pivotY = r.yMin + pivot.y * r.height;
        rect.SetLocalComputeRect(new Rect(pivotX - pivot.x * w, pivotY - pivot.y * h, w, h));

        foreach (var c in slot.Children) ReanchorAndDescend(c, rect);
        foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, rect);
    }

    private static bool SlotRunsLayout(Slot? slot)
    {
        var layout = slot?.GetComponent<LayoutController>();
        return layout != null && layout.Enabled.Value;
    }

    private static bool RectUnchanged(in Rect a, in Rect b)
        => a.Min.x == b.Min.x && a.Min.y == b.Min.y && a.Size.x == b.Size.x && a.Size.y == b.Size.y;

    // run this slot's layout (if any) and reflow each child's subtree against the new rects. - xlinka
    private void ApplyLayout(Slot slot, RectTransform rect)
    {
        var layout = slot.GetComponent<LayoutController>();
        if (layout == null || !layout.Enabled.Value) return;

        layout.PrepareCompute();
        layout.ArrangeChildren(rect.RectChildren);

        foreach (var child in slot.Children) ReflowAfterParentChanged(child, rect);
        foreach (var child in slot.LocalChildren) ReflowAfterParentChanged(child, rect);
    }

    // a child's rect was just overridden by its parent's layout. re-anchor descendants and re-run inner layout. - xlinka
    private void ReflowAfterParentChanged(Slot slot, RectTransform parent)
    {
        var rect = slot.GetComponent<RectTransform>();
        if (rect != null && LayoutSizing.IsIgnored(rect))
        {
            // The layout skipped it, so it still holds the rect it was anchored to before the parent was
            // placed (for a stretched overlay, the whole canvas). Anchor it to where the parent is now.
            ReanchorAndDescend(slot, parent);
            return;
        }
        if (rect != null)
        {
            // Descend the subtree EXACTLY ONCE. If this slot runs a layout, ApplyLayout arranges its children
            // and reflows them - re-anchoring them first would just be overwritten. Only when there's no
            // layout do the children keep anchor-relative positions and need re-anchoring here. The old code
            // did BOTH at every level -> O(2^depth) relayout, which froze deep trees like the Session form. -xlinka
            if (SlotRunsLayout(slot))
            {
                ApplyLayout(slot, rect);
            }
            else
            {
                foreach (var c in slot.Children) ReanchorAndDescend(c, rect);
                foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, rect);
            }
        }
        else
        {
            foreach (var c in slot.Children) ReflowAfterParentChanged(c, parent);
            foreach (var c in slot.LocalChildren) ReflowAfterParentChanged(c, parent);
        }
    }

    private void ReanchorAndDescend(Slot slot, RectTransform parent)
    {
        var rect = slot.GetComponent<RectTransform>();
        if (rect != null)
        {
            rect.SetLocalComputeRect(ComputeRect(rect, parent));
            // Re-anchoring undoes a content fit, which ComputeRects only applies on its own pass: fit again
            // first, so the children are placed once, in the fitted rect.
            FitToContent(slot, rect);
            // Descend once: a layout slot arranges + reflows its own children (ApplyLayout); a non-layout
            // slot re-anchors them. Doing both was the O(2^depth) blowup (see ReflowAfterParentChanged). -xlinka
            if (SlotRunsLayout(slot))
            {
                ApplyLayout(slot, rect);
            }
            else
            {
                foreach (var c in slot.Children) ReanchorAndDescend(c, rect);
                foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, rect);
            }
        }
        else
        {
            foreach (var c in slot.Children) ReanchorAndDescend(c, parent);
            foreach (var c in slot.LocalChildren) ReanchorAndDescend(c, parent);
        }
    }

    // Re-lay-out the subtree under a chunk root. The root's own rect is kept unless the root ITSELF
    // moved: a chunk root whose anchors or offsets changed (a Home widget re-sizing its own cell
    // footprint) is re-anchored against its parent first, or the new offsets would sit in the sync
    // members forever while the computed rect stayed where the last full pass put it. Only when the
    // parent runs no layout controller: under a layout the controller owns the rect, not the anchors.
    // Mirrors ComputeRects for the children only, so the parent's child list is untouched. -xlinka
    private void RelayoutChunkSubtree(GraphicChunkRoot root)
    {
        var slot = root.Slot;
        if (slot != Slot && !slot.ActiveSelf.Value)
            return;
        var rect = slot.GetComponent<RectTransform>();
        if (rect == null)
            return;

        var parent = rect.RectParent;
        if (parent != null && (rect.DataModelFlags & RectTransform.DataModelFlag.RectChanged) != 0
            && !SlotRunsLayout(parent.Slot))
        {
            rect.SetLocalComputeRect(ComputeRect(rect, parent));
            rect.ClearDataModelFlags(RectTransform.DataModelFlag.RectChanged);
        }
        else if (rect.BlendedBy != null)
        {
            rect.SetLocalComputeRect(rect.PlacedComputeRect);
        }

        // Re-measure this subtree's metrics before re-arranging it (scoped path mirror of the full pump).
        MeasureRects(slot);

        rect.ClearRectChildren();
        foreach (var child in slot.Children)
            ComputeRects(child, rect);
        foreach (var child in slot.LocalChildren)
            ComputeRects(child, rect);
        // Under a parent's layout the parent owns the root's size, as on the full pass, where the parent's
        // arrangement runs after the fit and overrides it.
        if (parent == null || !SlotRunsLayout(parent.Slot))
        {
            ApplyContentSizeFit(slot, rect);
            ApplyAspectRatio(slot, rect);
        }
        ApplyLayout(slot, rect);
    }

    private void ApplyScrollRects(Slot slot)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return;
        }

        // By index: a scroll sitting on its own content gives that slot its chunk root on the first pass, which
        // adds to the very list being walked.
        var comps = slot.Components;
        for (int ci = 0; ci < comps.Count; ci++)
        {
            if (comps[ci] is not ScrollRect scroll || !scroll.Enabled.Value)
            {
                continue;
            }

            if (scroll.ApplyScroll(out var content) && content != null)
            {
                ApplyLayout(content.Slot, content);
            }
        }

        foreach (var child in slot.Children)
        {
            ApplyScrollRects(child);
        }
        foreach (var child in slot.LocalChildren)
        {
            ApplyScrollRects(child);
        }
    }

    // Aggregate the active UI bounds (post-layout) and, when enabled, size the canvas collider to fit.
    // Runs once per layout-dirty cycle (after ComputeRects/ApplyScrollRects so the rects are final), NOT on
    // the hover/visual path. With SizeCollider off it only computes the root BoundingRect (available for a
    // cheap hit-test reject); it never touches scroll/layout output, so the scroll fix is unaffected. -xlinka
    private void UpdateCanvasBounds()
    {
        _hitBoundsStale = true;
        if (_rootRect == null || !SizeCollider.Value || _uiCollider == null) return;
        _rootRect.UpdateBounds();
        var b = _rootRect.BoundingRect;
        if (b.IsEmpty) return;
        // Flat slab in canvas-local space; tiny z keeps the Godot box shape from being degenerate.
        SetDerived(_uiCollider.Size, new float3(b.width, b.height, 0.001f));
        SetDerived(_uiCollider.Offset, CanvasToLocal(b.Center));
    }

    // Every peer lays the canvas out the same way and arrives at the same box, so a box that already matches is
    // left alone, and one this machine may not write (a panel in somebody else's hand) is left to whoever may:
    // the write would only be refused, and racing a pickup it was refused at the host and counted. -xlinka
    private static void SetDerived<T>(Sync<T> field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field.Value, value) && field.IsWriteAllowedQuietly())
            field.Value = value;
    }

    private static Rect ComputeRect(RectTransform rect, RectTransform? parent)
    {
        if (parent == null)
        {
            return Rect.FromMinMax(rect.OffsetMin.Value, rect.OffsetMax.Value);
        }

        var parentRect = parent.LocalComputeRect;
        var parentMin = parentRect.Min;
        var parentSize = parentRect.Size;
        var min = parentMin + parentSize * rect.AnchorMin.Value + rect.OffsetMin.Value;
        var max = parentMin + parentSize * rect.AnchorMax.Value + rect.OffsetMax.Value;
        return Rect.FromMinMax(min, max);
    }

    // A sphere round what the canvas answers for, tested before its plane: a laser asks every canvas in the
    // world every frame, most of them nowhere near the beam, and the plane test takes the slot's transform
    // apart twice. An open graph is a canvas a card. -xlinka
    private bool MightReach(in float3 rayOrigin, in float3 rayDirection, float maxDistance)
    {
        EnsureRoot();
        if (_rootRect == null || Slot == null)
        {
            return false;
        }
        if (_hitBoundsStale)
        {
            RefreshHitBounds();
        }
        if (!_hitBoundsValid)
        {
            return true;
        }
        float length = rayDirection.Length;
        if (length <= 0.000001f)
        {
            return false;
        }
        var rect = _hitBounds;
        var scale = Slot.GlobalScale;
        float largest = MathF.Max(MathF.Abs(scale.x), MathF.Max(MathF.Abs(scale.y), MathF.Abs(scale.z)));
        float radius = MathF.Sqrt(rect.width * rect.width + rect.height * rect.height) * 0.5f * largest;
        var center = Slot.LocalPointToGlobal(CanvasToLocal(rect.Center));
        var toward = center - rayOrigin;
        float along = float3.Dot(toward, rayDirection / length);
        if (along < -radius || along - radius > maxDistance)
        {
            return false;
        }
        return toward.LengthSquared - along * along <= radius * radius;
    }

    private bool TryRayToCanvasPoint(
        float3 rayOrigin,
        float3 rayDirection,
        out UIInteractionContext context,
        UIInteractionSource source,
        int pointerId,
        User? actor = null)
    {
        context = default;
        EnsureRoot();

        if (_rootRect == null || Slot == null || !Slot.IsActive)
        {
            return false;
        }

        float dirLength = rayDirection.Length;
        if (dirLength <= 0.000001f)
        {
            return false;
        }

        var normalizedDirection = rayDirection / dirLength;
        var localOrigin = Slot.GlobalPointToLocal(rayOrigin);
        var localDirection = Slot.GlobalDirectionToLocal(normalizedDirection);
        if (MathF.Abs(localDirection.z) <= 0.000001f)
        {
            return false;
        }

        float t = -localOrigin.z / localDirection.z;
        if (t < 0f)
        {
            return false;
        }

        var localPoint3 = localOrigin + localDirection * t;
        var localPoint = LocalToCanvas(localPoint3);
        var worldPoint = Slot.LocalPointToGlobal(localPoint3);
        float distance = (worldPoint - rayOrigin).Length;

        context = new UIInteractionContext(
            this,
            source,
            pointerId,
            localPoint,
            worldPoint,
            rayOrigin,
            normalizedDirection,
            distance,
            actor);
        return true;
    }

    // Two spaces here, and mixing them up is the whole bug this guards against:
    //  - CANVAS-VISIBLE space is where the pointer is (context.LocalPoint) and where pixels land. Clip windows
    //    accumulate in this space, so a viewport mask keeps clipping where it actually sits and a row scrolled
    //    out of view is rejected by that window before its own rect is ever tested.
    //  - CONTENT space is a slot's raw layout rect. Rect/graphic tests happen there, against the pointer with
    //    the accumulated scroll displacement taken back off.
    // scroll is that displacement, summed on the way down (SlotScrollStep per slot) - the same composition the
    // shader gets from CumulativeScrollOffset -> clip_offset. A mask window is pushed the other way (Translate
    // by the scroll AT THE MASK) so it lands on the pixels the mask is really covering. Before this, the scan
    // read every rect at scroll 0, so a scrolled list handed presses to whatever row USED to be there. -xlinka
    private void ScanHitSlot(Slot slot, in UIInteractionContext context, ref HitCandidate candidate, Rect? clipRect, float2 scroll)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return;
        }

        if (clipRect.HasValue && !clipRect.Value.Contains(context.LocalPoint))
        {
            return;
        }

        scroll += SlotHitStep(slot);
        var point = context.LocalPoint - scroll;

        // Index iteration (not foreach) so this per-frame, per-slot scan doesn't allocate an enumerator on
        // the IReadOnlyList each call. -xlinka
        var comps = slot.Components;
        bool captured = false;
        for (int ci = 0; ci < comps.Count; ci++)
        {
            switch (comps[ci])
            {
                case InteractionBlock block when block.BlocksPoint(point):
                    candidate = HitCandidate.Blocked;
                    break;
                case IUIInteractionCapture capture when capture.CanInteract && capture.IsPointInside(point) && capture.CapturesSubtree(in context):
                    candidate = new HitCandidate(capture);
                    captured = true;
                    break;
                case IUIInteractable interactable when interactable.CanInteract && interactable.IsPointInside(point):
                    candidate = new HitCandidate(interactable);
                    break;
                case HoverArea area when _areaScan != null && area.Covers(point):
                    _areaScan.Add(area);
                    break;
            }
        }

        // A capturing element owns everything below its slot: the widget grid in edit mode takes the
        // pointer over the whole widget, buttons and fields inside it never see it. -xlinka
        if (captured)
        {
            return;
        }

        var nextClip = clipRect;
        var mask = slot.GetComponent<Mask>();
        var rect = slot.GetComponent<RectTransform>();
        if (mask != null && mask.Enabled.Value && rect != null)
        {
            var maskRect = Translate(rect.LocalComputeRect, scroll);
            nextClip = nextClip.HasValue
                ? nextClip.Value.Intersection(maskRect)
                : maskRect;
            if (nextClip.Value.IsEmpty)
            {
                return;
            }
        }

        var children = slot.Children;
        for (int i = 0; i < children.Count; i++)
        {
            ScanHitSlot(children[i], in context, ref candidate, nextClip, scroll);
        }
        var localChildren = slot.LocalChildren;
        for (int i = 0; i < localChildren.Count; i++)
        {
            ScanHitSlot(localChildren[i], in context, ref candidate, nextClip, scroll);
        }
    }

    private static int PointerKey(UIInteractionSource source, int pointerId)
    {
        return ((int)source << 24) ^ pointerId;
    }

    private bool DispatchAxis(IUIInteractable interactable, in UIInteractionContext context, in float2 axis)
    {
        foreach (var receiver in GetInteractionReceivers<IUIAxisActionReceiver>(interactable))
        {
            if (receiver.ProcessAxis(in context, in axis))
            {
                return true;
            }
        }

        return false;
    }

    private bool DispatchSecondary(IUIInteractable interactable, in UIInteractionContext context)
    {
        foreach (var receiver in GetInteractionReceivers<IUISecondaryActionReceiver>(interactable))
        {
            if (receiver.TriggerSecondary(in context))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<T> GetInteractionReceivers<T>(IUIInteractable interactable) where T : class
    {
        var directReceiver = interactable as T;
        if (directReceiver != null)
        {
            yield return directReceiver;
        }

        if (interactable is not Component component || component.Slot == null)
        {
            yield break;
        }

        var current = component.Slot;
        bool first = true;
        while (current != null)
        {
            if (!first && current.GetComponent<SearchBlock>() != null)
            {
                yield break;
            }

            foreach (var receiver in current.GetComponentsImplementing<T>())
            {
                if (ReferenceEquals(receiver, directReceiver))
                {
                    continue;
                }

                yield return receiver;
            }

            if (ReferenceEquals(current, Slot))
            {
                yield break;
            }

            first = false;
            current = current.Parent;
        }
    }

    // Render a slot subtree into one chunk's mesh, stopping at any nested GraphicChunkRoot (those
    // render into their own chunk). ownRoot is the chunk root we're rendering for (null = root). - xlinka
    private void RenderPartition(GraphicsChunk.RenderData rd, Slot slot, Rect? clipRect, Rect? ridingClip, GraphicChunkRoot? ownRoot, StencilRole stencil = StencilRole.None)
    {
        if (slot != Slot && !slot.ActiveSelf.Value)
        {
            return;
        }

        if ((clipRect.HasValue && clipRect.Value.IsEmpty) || (ridingClip.HasValue && ridingClip.Value.IsEmpty))
        {
            return;
        }

        var mask = slot.GetComponent<Mask>();
        var rect = slot.GetComponent<RectTransform>();
        bool isMask = mask != null && mask.Enabled.Value && rect != null;
        bool stencilMask = isMask && mask!.StencilMasking.Value && StencilMaskingEnabled;
        bool showOwnGraphics = !isMask || mask!.ShowMaskGraphic.Value;

        mask?.PrepareCompute();

        if (stencilMask)
        {
            // A stencil mask stamps its SHAPE into the stencil buffer (Write pass). Queued BEFORE the
            // children recurse, and render_priority follows submission order, so the writer draws first and
            // the tested content draws after - even though the mask itself is invisible. -xlinka
            EmitGraphics(rd, slot, clipRect, ridingClip, StencilRole.Write);
            // The writer draws nothing, and writers sort ahead of everything else in the chunk, so a writer
            // made visible would sit under the backgrounds before it. A mask that shows its graphic draws it
            // a second time as an ordinary graphic, in its own place in the tree. -xlinka
            if (showOwnGraphics)
                EmitGraphics(rd, slot, clipRect, ridingClip, stencil);
        }
        else if (showOwnGraphics)
        {
            EmitGraphics(rd, slot, clipRect, ridingClip, stencil);
        }

        var nextClip = clipRect;
        var nextRiding = ridingClip;
        if (isMask)
        {
            // A mask BELOW the chunk root always moves with this chunk's geometry, so on a scroll-riding chunk
            // it belongs in the riding window (geometry trim). On a static chunk nothing moves and it stays in
            // the material window, exactly as before. -xlinka
            if (_chunkRidesScroll)
            {
                nextRiding = nextRiding.HasValue
                    ? nextRiding.Value.Intersection(rect!.LocalComputeRect)
                    : rect!.LocalComputeRect;
                if (nextRiding.Value.IsEmpty)
                {
                    return;
                }
            }
            else
            {
                nextClip = nextClip.HasValue
                    ? nextClip.Value.Intersection(rect!.LocalComputeRect)
                    : rect!.LocalComputeRect;
                if (nextClip.Value.IsEmpty)
                {
                    return;
                }
            }
        }

        // Content under a stencil mask is stencil-TESTED; otherwise it inherits our role.
        var nextStencil = stencilMask ? StencilRole.Test : stencil;

        foreach (var child in slot.Children)
        {
            RenderChildPartition(rd, child, nextClip, nextRiding, ownRoot, nextStencil);
        }
        foreach (var child in slot.LocalChildren)
        {
            RenderChildPartition(rd, child, nextClip, nextRiding, ownRoot, nextStencil);
        }
    }

    // Queue a slot's own enabled graphics for the worker emit pass, tagging each with its stencil role. -xlinka
    private void EmitGraphics(GraphicsChunk.RenderData rd, Slot slot, Rect? clipRect, Rect? ridingClip, StencilRole stencil)
    {
        // Indexed scan: GetComponents allocated a LINQ iterator + a copy list PER SLOT PER REBUILD.
        var comps = slot.Components;
        for (int ci = 0; ci < comps.Count; ci++)
        {
            if (comps[ci] is not Graphic graphic || !graphic.Enabled.Value)
            {
                continue;
            }

            // MAIN: snapshot + glyph rasterization stay here (Godot-touching). The geometry
            // build (ComputeGraphic) is deferred to the worker via the chunk's emit queue.
            graphic.PrepareCompute();
            if (graphic.RequiresPreGraphicsCompute)
            {
                var preGraphics = graphic.PreGraphicsCompute();
                if (!preGraphics.IsCompletedSuccessfully)
                {
                    preGraphics.AsTask().GetAwaiter().GetResult();
                }
            }

            // Clip elision: if the inherited clip fully encloses this graphic's bounds it discards nothing,
            // so drop it - the graphic then batches into the shared unclipped submesh instead of spawning a
            // per-clip material clone + an extra Godot surface. Only sound for quad graphics bounded by their
            // rect (Image family), never text (glyphs can overflow the rect), never a stencil graphic (its
            // key must keep the shape). Common win: scroll items fully in view, content well inside a panel
            // mask. -xlinka
            var effectiveClip = clipRect;
            var effectiveRiding = ridingClip;
            if (effectiveRiding.HasValue && !graphic.TrimsGeometryToClip)
            {
                // This graphic never trims its own geometry, so a material window is the only clip it can get.
                // Fold the riding half back in there: it goes stale as the chunk scrolls, exactly as it did
                // before the split, but it's never left with LESS clipping than it had. -xlinka
                effectiveClip = effectiveClip.HasValue
                    ? effectiveClip.Value.Intersection(effectiveRiding.Value)
                    : effectiveRiding;
                effectiveRiding = null;
            }

            if (effectiveClip.HasValue && !_chunkRidesScroll && stencil == StencilRole.None && IsRectBoundedGraphic(graphic))
            {
                var grect = slot.GetComponent<RectTransform>();
                if (grect != null && effectiveClip.Value.Encloses(grect.LocalComputeRect))
                {
                    effectiveClip = null;
                }
            }

            // Measure here, on the main thread, while the rect tree is walkable - the worker can only read
            // what the queue carries, and the chunk needs this box to batch surfaces. -xlinka
            rd.QueueGraphic(graphic, effectiveClip, effectiveRiding, stencil, graphic.MeasureBounds());
        }
    }

    // Graphics whose geometry never exceeds their RectTransform rect, so an enclosing clip is a no-op for
    // them. Text is excluded - glyph ascenders/descenders can overflow the layout rect. -xlinka
    private static bool IsRectBoundedGraphic(Graphic graphic)
        => graphic is Image or RawImage or TiledRawImage or BorderedImage;

    private void RenderChildPartition(GraphicsChunk.RenderData rd, Slot child, Rect? clip, Rect? ridingClip, GraphicChunkRoot? ownRoot, StencilRole stencil = StencilRole.None)
    {
        // An inactive child renders nothing - and, critically, must NOT register its chunk root as "seen"
        // this cycle. If it did, CleanupChunks wouldn't disable that chunk, and a hidden overlay/screen
        // (a canvas-root modal like the create-world dialog) keeps drawing on top of the next screen -
        // the "two screens at once" overlap. RenderPartition and RegisterNestedChunkRoots already skip
        // inactive slots before touching their chunk roots; this discovery path was the one that didn't. -xlinka
        if (!child.ActiveSelf.Value)
            return;

        var childRoot = child.GetComponent<GraphicChunkRoot>();
        if (childRoot != null && !ReferenceEquals(childRoot, ownRoot) && child.GetComponent<RectTransform>() != null)
        {
            // chunk boundary: register it and skip - it renders into its own mesh
            _partitionFoundChunk = true;
            EnsureChunk(childRoot);
            if (_seenChunkRoots.Add(childRoot)) _chunkOrder.Add(childRoot);
            return;
        }
        RenderPartition(rd, child, clip, ridingClip, ownRoot, stencil);
    }

    private static UIInteractionSource GetInteractionSource(InteractionLaser laser)
    {
        var input = Engine.Current?.InputInterface;
        if (input != null && !input.IsVRActive)
        {
            return UIInteractionSource.Desktop;
        }

        return laser.ControllerSide.Value == Chirality.Left
            ? UIInteractionSource.VRLeft
            : UIInteractionSource.VRRight;
    }

    private static int GetPointerId(InteractionLaser laser)
    {
        return laser.ControllerSide.Value == Chirality.Left ? 1 : 2;
    }

    private static User? GetInteractionActor(InteractionLaser laser)
    {
        return laser?.Slot?.ActiveUser ?? laser?.World?.LocalUser;
    }

    private readonly struct HitCandidate
    {
        public static readonly HitCandidate Blocked = new HitCandidate(null);

        public readonly IUIInteractable? Interactable;

        public HitCandidate(IUIInteractable? interactable)
        {
            Interactable = interactable;
        }
    }
}
