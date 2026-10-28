// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Logging;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Helio.UI;

public sealed class GraphicsChunk
{
    public sealed class RenderData
    {
        private const int DefaultLogicalRenderQueue = 3000;
        private const int GodotRenderPriorityMin = -128;
        private const int GodotRenderPriorityMax = 127;
        private const int OverlayCanvasBandLow = 112;

        private readonly Dictionary<MaterialKey, List<PhosTriangleSubmesh>> _requestedMaterials = new();
        // Captured on the main thread (prepare walk), drained on the worker (EmitQueued). The worker
        // never traverses the live slot tree - it only iterates this queue and calls ComputeGraphic,
        // which reads each graphic's already-snapshotted state + stable LocalComputeRect. Bounds is the
        // canvas-local box the graphic's geometry stays inside, measured on the main thread too. - xlinka
        private readonly List<(Graphic Graphic, Rect? Clip, Rect? RidingClip, StencilRole Stencil, Rect? Bounds)> _emitQueue = new();
        private readonly GraphicsChunk _chunk;
        // Every submesh this chunk has opened this pass, in creation order, with the box its geometry covers
        // so far and the stencil role it was opened under. Surface order IS draw order, so both are what
        // GetSubmesh needs to work out whether a graphic can drop back into an earlier surface. -xlinka
        private readonly List<SubmeshSpan> _submeshSpans = new();
        private readonly Dictionary<PhosTriangleSubmesh, int> _spanOfSubmesh = new();
        // The graphic currently being emitted: the first span it opened, the earlier spans it reached back
        // into, and the box it covers (null = we don't know how far it spreads). -xlinka
        private int _graphicFirstSpan;
        private readonly List<int> _graphicAdoptedSpans = new();
        private Rect? _graphicBounds;
        private Rect? _clipRect;
        private Rect? _ridingClipRect;
        private StencilRole _stencilRole;
        private bool _graphicTrims;
        // Latch so the render-priority band-exhaustion warning fires once per RenderData, not every submit. -xlinka
        private bool _loggedBandExhaustion;
        // The last submit drew a sheet wall as surface 0, with this material.
        private bool _hasWall;
        private MaterialMap _wallMap;

        public PhosMesh Mesh { get; } = new();
        public Rect? ClipRect => _clipRect;

        // The fastest animated picture this chunk drew playing (its frames a second), 0 for none. The material
        // moves its frames on the GPU clock; a host that captures the canvas only on change has to capture again
        // for each frame, and the canvas reads this to know how often.
        public float PlayingFps { get; private set; }

        public void NotePlaying(float fps)
        {
            if (fps > PlayingFps)
                PlayingFps = fps;
        }

        // Scroll content is baked ONCE and moved by the clip_offset uniform, so items off-screen at bake time
        // still have to be in the mesh (they scroll into view later) and on-screen items must NOT be trimmed to
        // the FIXED viewport window (the shader clips them at render). When true, graphics stop trimming against
        // the material clip rect and trim against the RIDING window instead (see GeometryClipRect). -xlinka
        public bool SuppressGeometryClip { get; set; }

        // Clip rect for GEOMETRY culling/trimming. Static chunk: the same window the material carries (trim +
        // shader clip, as always). Scroll-riding chunk: only the RIDING window - a mask that moves with this
        // chunk's geometry (a mask inside it, or an ancestor mask locked to the same scroll offset). Their
        // relationship to the verts is fixed, so trimming at bake is exact and stays exact however far the
        // chunk slides; a material rect is a FIXED canvas-space window and cannot ride a live offset, which is
        // why the riding half must not go there. The static half stays on the material (GetSubmesh reads
        // _clipRect) where clip_offset moves the test point to match. -xlinka
        public Rect? GeometryClipRect => SuppressGeometryClip ? _ridingClipRect : _clipRect;

        public RenderData(GraphicsChunk chunk)
        {
            _chunk = chunk;
            PrepareMesh();
        }

        public void SetClipRect(Rect? clipRect, Rect? ridingClipRect = null)
        {
            _clipRect = clipRect;
            _ridingClipRect = ridingClipRect;
        }

        public void PrepareCompute()
        {
            PlayingFps = 0f;
            Mesh.Clear();
            _requestedMaterials.Clear();
            _emitQueue.Clear();
            _submeshSpans.Clear();
            _spanOfSubmesh.Clear();
            _graphicFirstSpan = 0;
            _graphicAdoptedSpans.Clear();
            _graphicBounds = null;
            PrepareMesh();
        }

        // bounds = the canvas-local box this graphic's geometry stays inside, or null when its extent isn't
        // known. Everything the graphic opens from here until the next BeginGraphic belongs to it. -xlinka
        public void BeginGraphic(Rect? bounds)
        {
            _graphicFirstSpan = _submeshSpans.Count;
            _graphicAdoptedSpans.Clear();
            _graphicBounds = bounds;
        }

        // MAIN: queue a prepared graphic (with its computed clip + stencil role) for the worker emit pass.
        // ridingClip is the half of the window that moves with this chunk (geometry trim only, never keyed
        // into a material) - see GeometryClipRect. bounds comes from Graphic.MeasureBounds and has to be
        // taken here, on the main thread, because the worker can't walk the rect tree.
        public void QueueGraphic(Graphic graphic, Rect? clip, Rect? ridingClip = null, StencilRole stencil = StencilRole.None, Rect? bounds = null)
        {
            _emitQueue.Add((graphic, clip, ridingClip, stencil, bounds));
        }

        // WORKER: build the geometry for every queued graphic. No slot/datamodel access (materials
        // were keyed by identity and are resolved at submit), no tree traversal - just each graphic's
        // ComputeGraphic reading its snapshot + stable LocalComputeRect into this chunk's mesh.
        public void EmitQueued()
        {
            for (int i = 0; i < _emitQueue.Count; i++)
            {
                var (graphic, clip, ridingClip, stencil, bounds) = _emitQueue[i];
                BeginGraphic(bounds);
                SetClipRect(clip, ridingClip);
                _stencilRole = stencil;
                _graphicTrims = graphic.TrimsGeometryToClip;
                graphic.ComputeGraphic(this);
            }
            _graphicTrims = false;
        }

        // The clip window a material carries. A graphic that trims its own geometry to the window on a chunk
        // that does not scroll has nothing left outside it for the shader to cut, so its material leaves the
        // window out, and graphics clipped by different masks share one material and can share a surface. A
        // card full of masked labels was a material clone and a draw call per label. Scrolled content keeps
        // the window: it is baked whole and cut at render. -xlinka
        private Rect? MaterialClip => _graphicTrims && !SuppressGeometryClip ? null : _clipRect;

        public PhosTriangleSubmesh GetSubmesh(IAssetProvider<MaterialAsset>? material)
            => GetSubmesh(new MaterialKey(material, null, null, MaterialClip, _stencilRole));

        public PhosTriangleSubmesh GetSubmesh(IAssetProvider<MaterialAsset>? material, MaterialMapper mapper)
            => GetSubmesh(new MaterialKey(material, null, mapper, MaterialClip, _stencilRole));

        public PhosTriangleSubmesh GetSubmesh(IAssetProvider<MaterialAsset>? material, object? key, MaterialMapper? mapper)
            => GetSubmesh(new MaterialKey(material, key, mapper, MaterialClip, _stencilRole));

        // Batching rule. A surface is a draw call and surface order is draw order, so the old rule was simply
        // "one surface per graphic per material" - never reach back past another graphic, and order can't
        // possibly be wrong. That is also why the controls page opened ~500 surfaces and walked into Godot's
        // hard 256-surface ceiling, where the extras are dropped on the floor and the labels go blank.
        //
        // A graphic can share an earlier surface of the same material as long as its box touches nothing that
        // was opened AFTER that surface. Those later surfaces are the only ones that currently draw over it;
        // if we cover none of them, moving back is invisible. Inside one surface the triangles keep emission
        // order, so we still land on top of whatever is already in there, which is where we belong. A graphic
        // whose extent we can't measure, or one with a different stencil role in the way, gets its own surface
        // exactly like before. Worst case that is the old behaviour, so this never opens MORE surfaces. -xlinka
        public PhosTriangleSubmesh GetSubmesh(in MaterialKey key)
        {
            if (!_requestedMaterials.TryGetValue(key, out var submeshes))
            {
                submeshes = new List<PhosTriangleSubmesh>();
                _requestedMaterials.Add(key, submeshes);
            }

            if (submeshes.Count > 0)
            {
                var candidate = submeshes[submeshes.Count - 1];
                int span = _spanOfSubmesh[candidate];
                // A surface this graphic is already writing into stays its own, whether it opened it or
                // reached back into it. That is what the old per-graphic floor did: a graphic that emits into
                // several materials at once (text with inline sprites, marks under glyphs) keeps hopping
                // between them, and re-testing each hop would open a surface per hop. Its own parts are
                // laid out not to overlap, so the order inside one graphic was never in question. -xlinka
                if (span >= _graphicFirstSpan || _graphicAdoptedSpans.Contains(span))
                {
                    CoverSpan(span);
                    return candidate;
                }

                if (CanReachBack(span))
                {
                    _graphicAdoptedSpans.Add(span);
                    CoverSpan(span);
                    return candidate;
                }
            }

            var submesh = new PhosTriangleSubmesh(Mesh);
            Mesh.Submeshes.Add(submesh);
            submeshes.Add(submesh);
            _spanOfSubmesh[submesh] = _submeshSpans.Count;
            _submeshSpans.Add(new SubmeshSpan
            {
                Bounds = _graphicBounds ?? default,
                BoundsKnown = _graphicBounds.HasValue,
                Stencil = _stencilRole,
            });
            return submesh;
        }

        // Everything opened after this span draws over it. Reaching back is only safe when the current
        // graphic covers none of that and no stencil role changed on the way - a mask writer has to stay in
        // front of the content it masks, so a role change counts as an overlap and stops us dead. -xlinka
        private bool CanReachBack(int span)
        {
            if (!_graphicBounds.HasValue)
            {
                return false;
            }

            var bounds = _graphicBounds.Value;
            for (int i = span + 1; i < _submeshSpans.Count; i++)
            {
                var later = _submeshSpans[i];
                if (later.Stencil != _stencilRole || !later.BoundsKnown || later.Bounds.Overlaps(bounds))
                {
                    return false;
                }
            }

            return true;
        }

        // Grow a span's box by what the current graphic just put in it. Once anything unmeasured lands in a
        // span the box is a lie, so it stays marked unknown and nothing may reach back past it. -xlinka
        private void CoverSpan(int index)
        {
            var span = _submeshSpans[index];
            if (!span.BoundsKnown)
            {
                return;
            }

            if (_graphicBounds.HasValue)
            {
                span.Bounds = span.Bounds.Encapsulate(_graphicBounds.Value);
            }
            else
            {
                span.Bounds = default;
                span.BoundsKnown = false;
            }

            _submeshSpans[index] = span;
        }

        public T AttachPropertyBlock<T>() where T : Component, IAssetProvider<MaterialPropertyBlockAsset>, new()
        {
            _chunk.SetupComponents();
            return _chunk.ChunkSlot.AddLocalSlot("PropertyBlock").AttachComponent<T>();
        }

        // Deferred material resolution. The compute/emit pass keys submeshes by texture/atlas
        // IDENTITY (no slot creation), and these run at SUBMIT time on the main thread to create
        // the shared material + property-block slots. Keeping slot creation out of the emit pass
        // is what lets that pass run off the main thread. Static (no capture) so the delegate
        // reference is stable and submeshes with the same texture/atlas still batch together.
        public static readonly MaterialMapper ImageTexture =
            (rd, baseMaterial, key, usingDefault) =>
            {
                // A texture destroyed between emit and submit (a rebuilt row taking its preview with it) is keyed
                // on a submesh the next rebuild drops. Binding it to a material throws and aborts the whole submit
                // cycle, so it goes out bare for this one submit. -xlinka
                if (key is not IAssetProvider<TextureAsset> texture || texture.IsDestroyed)
                {
                    return new MaterialMap(baseMaterial);
                }

                // Default-material image: carry the texture on a shared per-texture material so the per-surface
                // clone samples it directly and live uniform writes (the scroll clip_offset) reach it. A custom
                // material keeps the property-block path - its variant is cached, so it won't scroll-clip, but
                // custom-material images aren't the scrollable list case. -xlinka
                return usingDefault
                    ? new MaterialMap(rd.GetSharedImageMaterial(texture))
                    : new MaterialMap(baseMaterial, rd.GetSharedImageBlock(texture));
            };

        // The same for an animated picture shown still: its first frame, from a material that does not play it.
        public static readonly MaterialMapper ImageTextureStill =
            (rd, baseMaterial, key, usingDefault) =>
            {
                if (key is not IAssetProvider<TextureAsset> texture || texture.IsDestroyed)
                    return new MaterialMap(baseMaterial);
                return usingDefault
                    ? new MaterialMap(rd.GetSharedImageMaterial(texture, still: true))
                    : new MaterialMap(baseMaterial, rd.GetSharedImageBlock(texture));
            };

        public static readonly MaterialMapper TextAtlas =
            (rd, baseMaterial, key, usingDefault) =>
                key is TextureAsset atlas ? new MaterialMap(rd.GetSharedTextMaterial(atlas)) : new MaterialMap(baseMaterial);

        public UITextMaterial GetSharedTextMaterial(TextureAsset atlas) => _chunk.GetSharedTextMaterial(atlas);

        public MainTexturePropertyBlock GetSharedImageBlock(IAssetProvider<TextureAsset> texture) => _chunk.GetSharedImageBlock(texture);

        public UIUnlitMaterial GetSharedImageMaterial(IAssetProvider<TextureAsset> texture, bool still = false) => _chunk.GetSharedImageMaterial(texture, still);

        internal void SubmitChanges(int sortingOrder)
        {
            _chunk.SetupComponents();
            EnsureComponents();
            AssignMaterials();

            _chunk.MeshRenderer!.SortingOrder.Value = sortingOrder;
            // Opt-in unbounded ordering: tell the hook to split this chunk's surfaces into per-surface
            // instances ordered by SortingOffset instead of the render_priority bands. Default false. -xlinka
            _chunk.MeshRenderer.PerSurfaceOrdering = Canvas.UnboundedRenderOrder;
            ApplyWallLead();
            _chunk.MeshRenderer.Enabled.Value = Mesh.VertexCount > 0;
            _chunk.MeshProvider!.SetMesh(Mesh);

            // MeshHook will ClearSurfaces + AddSurfaceFromArrays the underlying ArrayMesh, which
            // wipes Godot's surface override materials. The MeshRenderer's own SyncRef sees no
            // change, so we have to flag it ourselves or every surface past frame 0 renders bare. - xlinka
            _chunk.MeshRenderer.FlagSurfacesDirty();
        }

        private void AssignMaterials()
        {
            var requests = BuildMaterialRequests(out var submeshQueues);
            var wall = FindWall(requests);
            SortSubmeshesByRenderQueue(submeshQueues, wall);

            var surfaceIndexes = BuildSurfaceIndexes();
            int materialCount = surfaceIndexes.Count;
            _chunk.MeshRenderer!.Materials.EnsureExactCount(materialCount);
            _chunk.MeshRenderer.MaterialPropertyBlocks.EnsureExactCount(materialCount);
            _chunk.MeshRenderer.EnsureExactSurfaceRenderPriorityCount(materialCount);

            // Each surface needs its OWN Godot render_priority based on submission
            // order (tree depth-first, after SortSubmeshesByRenderQueue), AND its own
            // cloned material - MeshRendererHook applies priority by mutating
            // Material.RenderPriority in place, so if surfaces shared a material
            // their priorities would collapse to whichever value was written last.
            // Coplanar UI quads then fall back to Godot's distance sort, which
            // flips with camera angle and produces the "wash" artifact. - xlinka
            _hasWall = wall != null && surfaceIndexes.TryGetValue(wall, out int wallIndex) && wallIndex == 0;
            var perSurfacePriorities = BuildPerSurfacePriorities(materialCount, _chunk.RenderOnTop, _chunk.OverlayLevel,
                _chunk.Canvas?.DrawsOverlay == true, _chunk.ContainsChunks, _hasWall);
            // Every surface takes its clone on every submit, including the second and later surfaces of one key
            // (a material split by a stencil mark or text between them). Skipping one leaves it wearing the clone
            // of an older clip window, which cuts its geometry away once the window moves (a resized window).
            // SetMaterialTargets only writes a changed reference. -xlinka
            foreach (var request in requests)
            {
                foreach (var submesh in request.Submeshes)
                {
                    if (!surfaceIndexes.TryGetValue(submesh, out int surfaceIndex))
                    {
                        continue;
                    }

                    int priority = perSurfacePriorities[surfaceIndex];
                    _chunk.MeshRenderer.SetSurfaceRenderPriority(surfaceIndex, priority);
                    var clonedMaterial = _chunk.GetRenderPriorityMaterial(request.Map.FilteredMaterial, surfaceIndex, priority);
                    SetMaterialTargets(surfaceIndex, new MaterialMap(clonedMaterial, request.Map.FilteredPropertyBlock));
                    if (_hasWall && ReferenceEquals(submesh, wall))
                        _wallMap = request.Map;
                }
            }
        }

        // A sheet's wall is what its root draws first, when that writes depth: the backing of a window. It has
        // to be the first surface opened, before any stencil or clip came into play, or it is not the root's.
        private PhosTriangleSubmesh? FindWall(List<MaterialRequest> requests)
        {
            if (!_chunk.IsSheet || Mesh.Submeshes.Count == 0 || Mesh.Submeshes[0] is not PhosTriangleSubmesh first || first.IndexCount <= 0)
                return null;
            foreach (var request in requests)
            {
                if (!request.Submeshes.Contains(first))
                    continue;
                bool writesDepth = request.Map.FilteredMaterial is UIUnlitMaterial material && material.ZWrite.Value == ZWrite.On;
                return writesDepth && request.Key.Stencil == StencilRole.None ? first : null;
            }
            return null;
        }

        // The canvas moved this sheet's wall among the walls: only that surface's priority and clone change.
        internal void ApplyWallPriority()
        {
            var renderer = _chunk.MeshRenderer;
            if (!_hasWall || renderer == null || renderer.IsDestroyed || renderer.Materials.Count == 0)
                return;
            int priority = _chunk.WallPriority;
            renderer.SetSurfaceRenderPriority(0, priority);
            var clonedMaterial = _chunk.GetRenderPriorityMaterial(_wallMap.FilteredMaterial, 0, priority);
            SetMaterialTargets(0, new MaterialMap(clonedMaterial, _wallMap.FilteredPropertyBlock));
            ApplyWallLead();
        }

        // In the per-surface order there are no bands: the wall goes down by its key, which the canvas put
        // ahead of every sheet's own surfaces, nearest sheet first.
        private void ApplyWallLead()
        {
            var renderer = _chunk.MeshRenderer;
            if (renderer == null || renderer.IsDestroyed)
                return;
            float? lead = Canvas.UnboundedRenderOrder && _hasWall ? _chunk.WallSortKey : null;
            if (renderer.LeadSurfaceSortingOffset != lead)
                renderer.LeadSurfaceSortingOffset = lead;
        }

        // Godot caps per-surface render_priority at [-128..127] and sorts transparent surfaces by it
        // first, so coplanar UI quads need DISTINCT priorities or they fall back to distance sort (the
        // angle "wash"). We partition the range into bands so chunks layer deterministically even when
        // they overlap:
        //   root chunk        -> [-128 .. -W-1] (below everything)
        //   sheet walls       -> [  -W ..  -1]  (the backing of each StackLayer sheet, nearest sheet first; W is
        //                                        one slot a visible sheet, 16 at least, see Canvas.WallSlots)
        //   normal nested     -> [   0 ..  63]   (above root; chunks don't overlap each other)
        //   overlay level 1   -> [  64 ..  71]   (e.g. a modal's dim/blur backdrop)
        //   overlay level 2   -> [  72 ..  87]   (e.g. the modal panel background)
        //   overlay level >=3 -> [  88 .. 111]   (modal content/rows, on top)
        //   overlay canvas    -> [ 112 .. 127]   (the radial menu, the VR keyboard: over every world canvas)
        // Within a band, surfaces pack high in submission order (later = drawn on top), except a normal nested
        // chunk that holds other chunks: it packs low, under them. Packed high, a scroll pane's backing took
        // the top slot and covered every surface of its rows but their last one (a row's label drew under
        // the 75% pane once the row had two text surfaces). An overlay canvas's
        // root packs from the bottom of its band and its nested chunks from the top, so the two only meet
        // past sixteen surfaces. Its depth test is off; without a band of its own a world panel's lifted
        // header still drew over the menu from a metre behind it. - xlinka
        //
        // A sheet's wall takes the place the canvas ranked it at and the rest of its chunk packs as usual. Every
        // wall is down before anything else of any sheet, the nearest first, so the depth it lays down cuts away
        // everything of a lower sheet under it, that sheet's own backing included. Shared bands left whatever a
        // lower window drew at or below the upper backing's priority in the colour buffer before that backing,
        // and the glass showed it: a button of the window behind seen through the one in front, a whole window
        // behind as a darker block. -xlinka
        private int[] BuildPerSurfacePriorities(int materialCount, bool renderOnTop, int overlayLevel, bool overlayCanvas, bool containsChunks,
            bool hasWall)
        {
            var result = new int[materialCount];
            if (materialCount == 0) return result;

            int first = 0;
            if (hasWall)
            {
                result[0] = _chunk.WallPriority;
                first = 1;
            }
            int count = materialCount - first;
            if (count == 0) return result;

            if (overlayCanvas && !renderOnTop)
            {
                for (int i = 0; i < count; i++)
                    result[first + i] = System.Math.Min(GodotRenderPriorityMax, OverlayCanvasBandLow + i);
                return result;
            }

            int lo, hi;
            if (overlayCanvas)
            {
                lo = OverlayCanvasBandLow; hi = GodotRenderPriorityMax;
            }
            else if (!renderOnTop)
            {
                lo = GodotRenderPriorityMin; hi = -(_chunk.Canvas?.WallSlots ?? WallSlotsMin) - 1;  // root chunk
            }
            else if (overlayLevel <= 0)
            {
                lo = 0; hi = 63;                                // normal nested chunk
            }
            else if (overlayLevel == 1)
            {
                lo = 64; hi = 71;                               // overlay backdrop
            }
            else if (overlayLevel == 2)
            {
                lo = 72; hi = 87;                               // overlay panel
            }
            else
            {
                lo = 88; hi = OverlayCanvasBandLow - 1;         // overlay content
            }

            // When a chunk has more surfaces than its band is wide, the fill loop below saturates the
            // overflow at hi - those coplanar quads collapse onto one render_priority and Godot falls back to
            // distance sort (the angle "wash"). Surface that latent z-order bug in dev instead of shipping it
            // as a mystery visual glitch. Warn once per RenderData; near-exhaustion is debug-only. -xlinka
            int bandWidth = hi - lo + 1;
            if (count > bandWidth)
            {
                if (!_loggedBandExhaustion)
                {
                    _loggedBandExhaustion = true;
                    Logger.Warn($"[Helio] Render-priority band exhausted on chunk #{_chunk.OrderIndex} of canvas " +
                        $"'{_chunk.Canvas?.Slot?.Name?.Value ?? "?"}' " +
                        $"(renderOnTop={renderOnTop}, overlayLevel={overlayLevel}): {count} surfaces > band width " +
                        $"{bandWidth} [{lo}..{hi}]. {count - bandWidth} surface(s) saturate at {hi}, so coplanar " +
                        $"z-order goes ambiguous. Reduce overlay nesting / material count, or move this canvas to unbounded render order.");
                }
            }
            else if (count > bandWidth - 2 && Logger.EnableDebug)
            {
                Logger.Debug($"[Helio] Render-priority band near exhaustion on chunk #{_chunk.OrderIndex}: " +
                    $"{count}/{bandWidth} in [{lo}..{hi}].");
            }

            int start = renderOnTop && !overlayCanvas && overlayLevel <= 0 && containsChunks ? lo : hi - count + 1;
            if (start < lo) start = lo;
            for (int i = 0; i < count; i++)
            {
                result[first + i] = System.Math.Min(hi, start + i);
            }
            return result;
        }

        private List<MaterialRequest> BuildMaterialRequests(
            out Dictionary<PhosTriangleSubmesh, int> submeshQueues)
        {
            var requests = new List<MaterialRequest>(_requestedMaterials.Count);
            submeshQueues = new Dictionary<PhosTriangleSubmesh, int>();

            foreach (var pair in _requestedMaterials)
            {
                var map = GetMaterialMap(pair.Key);
                int logicalQueue = GetLogicalRenderQueue(map.FilteredMaterial);

                foreach (var submesh in pair.Value)
                {
                    if (submesh.IndexCount > 0)
                    {
                        submeshQueues[submesh] = logicalQueue;
                    }
                }

                requests.Add(new MaterialRequest(pair.Key, pair.Value, in map, logicalQueue));
            }

            return requests;
        }

        // A sheet's wall goes first of all: it is drawn in a band of its own, and the hook reads the chunk's sort
        // offset off its first surface.
        private void SortSubmeshesByRenderQueue(Dictionary<PhosTriangleSubmesh, int> submeshQueues, PhosTriangleSubmesh? wall)
        {
            // Stencil-WRITE submeshes must draw before any stencil-tested content (which compares against the
            // stencil they stamp), regardless of logical render queue. Without this, content with a custom low
            // RenderQueue could sort ahead of the mask writer and then be discarded by compare_equal. -xlinka
            HashSet<PhosTriangleSubmesh>? writeSubmeshes = null;
            foreach (var pair in _requestedMaterials)
            {
                if (pair.Key.Stencil != StencilRole.Write)
                {
                    continue;
                }

                writeSubmeshes ??= new HashSet<PhosTriangleSubmesh>();
                foreach (var submesh in pair.Value)
                {
                    writeSubmeshes.Add(submesh);
                }
            }

            if (Mesh.Submeshes.Count < 2 || (submeshQueues.Count < 2 && writeSubmeshes == null))
            {
                return;
            }

            var originalIndexes = new Dictionary<PhosSubmesh, int>(Mesh.Submeshes.Count);
            for (int i = 0; i < Mesh.Submeshes.Count; i++)
            {
                originalIndexes[Mesh.Submeshes[i]] = i;
            }

            Mesh.Submeshes.Sort((left, right) =>
            {
                if (wall != null && !ReferenceEquals(left, right) && (ReferenceEquals(left, wall) || ReferenceEquals(right, wall)))
                {
                    return ReferenceEquals(left, wall) ? -1 : 1;
                }

                // Writers first, always.
                bool leftWrite = writeSubmeshes != null && left is PhosTriangleSubmesh lw && writeSubmeshes.Contains(lw);
                bool rightWrite = writeSubmeshes != null && right is PhosTriangleSubmesh rw && writeSubmeshes.Contains(rw);
                if (leftWrite != rightWrite)
                {
                    return leftWrite ? -1 : 1;
                }

                int leftQueue = left is PhosTriangleSubmesh leftTriangle
                    && submeshQueues.TryGetValue(leftTriangle, out int lq)
                        ? lq
                        : DefaultLogicalRenderQueue;
                int rightQueue = right is PhosTriangleSubmesh rightTriangle
                    && submeshQueues.TryGetValue(rightTriangle, out int rq)
                        ? rq
                        : DefaultLogicalRenderQueue;

                int queueCompare = leftQueue.CompareTo(rightQueue);
                return queueCompare != 0
                    ? queueCompare
                    : originalIndexes[left].CompareTo(originalIndexes[right]);
            });
        }

        private static int GetLogicalRenderQueue(IAssetProvider<MaterialAsset>? material)
        {
            int queue = material switch
            {
                UIUnlitMaterial ui => ui.RenderQueue.Value,
                UITextMaterial text => text.RenderQueue.Value,
                { IsAssetAvailable: true } => material.Asset.RenderQueue,
                _ => DefaultLogicalRenderQueue,
            };

            return queue >= 0 ? queue : DefaultLogicalRenderQueue;
        }

        private Dictionary<PhosTriangleSubmesh, int> BuildSurfaceIndexes()
        {
            var indexes = new Dictionary<PhosTriangleSubmesh, int>();
            int surfaceIndex = 0;
            foreach (var submesh in Mesh.Submeshes)
            {
                if (submesh is not PhosTriangleSubmesh triangleSubmesh || triangleSubmesh.IndexCount <= 0)
                {
                    continue;
                }

                indexes[triangleSubmesh] = surfaceIndex++;
            }

            return indexes;
        }

        private void SetMaterialTargets(int index, in MaterialMap map)
        {
            var materialRef = _chunk.MeshRenderer!.Materials.GetElement(index);
            var material = map.FilteredMaterial;
            if (!ReferenceEquals(materialRef.Target, material))
            {
                materialRef.Target = material!;
            }

            var propertyBlockRef = _chunk.MeshRenderer.MaterialPropertyBlocks.GetElement(index);
            var propertyBlock = map.FilteredPropertyBlock;
            if (!ReferenceEquals(propertyBlockRef.Target, propertyBlock))
            {
                propertyBlockRef.Target = propertyBlock!;
            }
        }

        private void EnsureComponents()
        {
            _chunk.MeshProvider ??= _chunk.ChunkSlot.GetComponent<LocalMeshSource>() ?? _chunk.ChunkSlot.AttachComponent<LocalMeshSource>();
            if (_chunk.MeshRenderer == null)
            {
                _chunk.MeshRenderer = _chunk.ChunkSlot.GetComponent<MeshRenderer>() ?? _chunk.ChunkSlot.AttachComponent<MeshRenderer>();

                // No UI panel has ever wanted to be in a shadow map. A chunk is a flat, unlit, mostly
                // transparent quad and its silhouette in a cascade is either invisible or a black
                // rectangle on the floor, but MeshRenderer defaults to casting and every chunk inherited
                // that - so a room with twenty five canvases redrew a hundred and fifty odd chunk meshes
                // into four sun cascades every frame for nothing. Off at creation, before the hook ever
                // sees the renderer. -xlinka
                _chunk.MeshRenderer.ShadowCastMode.Value = ShadowCastMode.Off;
                _chunk.Canvas.ApplyViewDistance(_chunk.MeshRenderer);
            }
            _chunk.MeshRenderer.Mesh.Target = _chunk.MeshProvider;
        }

        private MaterialMap GetMaterialMap(in MaterialKey key)
        {
            var baseMaterial = key.BaseMaterial ?? _chunk.GetDefaultUIMaterial();
            bool usingDefault = key.BaseMaterial == null;

            MaterialMap map;
            if (key.Mapper != null)
            {
                map = key.Mapper(this, baseMaterial, key.Key, usingDefault);
            }
            else
            {
                map = new MaterialMap(baseMaterial);
            }

            if (key.Stencil != StencilRole.None)
            {
                // Mask shape (Write) or stencil-tested content (Test): a shader-variant clone, with the
                // inherited rect clip folded in as an orthogonal AABB bound alongside the stencil shape.
                return new MaterialMap(
                    _chunk.GetStencilMaterial(map.FilteredMaterial, key.Stencil, key.ClipRect),
                    map.FilteredPropertyBlock);
            }

            if (!key.ClipRect.HasValue)
            {
                return map;
            }

            return new MaterialMap(_chunk.GetClippedMaterial(map.FilteredMaterial, key.ClipRect.Value), map.FilteredPropertyBlock);
        }

        private void PrepareMesh()
        {
            Mesh.HasColors = true;
            Mesh.SetHasUV(0, true);
        }

        // One opened surface, in creation order. Bounds is the union of every graphic that landed in it, so
        // it only ever grows; BoundsKnown goes false the moment something unmeasured lands there. -xlinka
        private struct SubmeshSpan
        {
            public Rect Bounds;
            public bool BoundsKnown;
            public StencilRole Stencil;
        }

        private readonly struct MaterialRequest
        {
            public readonly MaterialKey Key;
            public readonly List<PhosTriangleSubmesh> Submeshes;
            public readonly MaterialMap Map;
            public readonly int LogicalRenderQueue;

            public MaterialRequest(
                in MaterialKey key,
                List<PhosTriangleSubmesh> submeshes,
                in MaterialMap map,
                int logicalRenderQueue)
            {
                Key = key;
                Submeshes = submeshes;
                Map = map;
                LogicalRenderQueue = logicalRenderQueue;
            }
        }
    }

    public Canvas Canvas { get; }
    public RectTransform Root { get; }
    public Slot ChunkSlot { get; private set; } = null!;
    public LocalMeshSource? MeshProvider { get; private set; }
    public MeshRenderer? MeshRenderer { get; private set; }
    public RenderData ContentRenderData { get; }

    // Nested chunks render above the root chunk: their surfaces pack into the top render_priority band. - xlinka
    public bool RenderOnTop { get; set; }

    // Other chunks sit inside this one (a scroll pane and its row chunks), so it is their background.
    public bool ContainsChunks { get; set; }

    // Render priorities kept for sheet walls, one a visible sheet, the nearest sheet lowest: [-W .. -1]. The band
    // takes one slot a sheet, never fewer than the minimum, and gives the root chunk back what it does not use.
    // The root keeps at least 32, so past the maximum the farthest sheets share a slot: they cannot show through
    // each other's content, only through each other's glass.
    internal const int WallSlotsMin = 16;
    internal const int WallSlotsMax = 96;

    // The root carries a StackLayer: a depth-writing backing it draws first is the sheet's wall.
    public bool IsSheet { get; set; }

    // This sheet's place among the walls of the canvas, ranked by Canvas.ApplyPlacements: its band priority, and
    // its key for the per-surface order.
    public int WallPriority { get; private set; } = -1;
    public float WallSortKey { get; private set; }

    internal void SetWallPriority(int priority, float sortKey)
    {
        if (WallPriority == priority && WallSortKey == sortKey)
            return;
        WallPriority = priority;
        WallSortKey = sortKey;
        ContentRenderData.ApplyWallPriority();
    }

    // Render-space translation of this chunk's whole mesh, applied on the chunk slot. ScrollRect uses it to
    // slide scrolled content without touching its rect; the canvas counter-translates the clip to keep the
    // viewport window fixed. Canvas pixels, in the chunk's (canvas-local) frame. -xlinka
    public float2 ComputeOffset { get; private set; }

    // Where the StackLayers above this chunk's root put it on top of its baked position: x and y slide it
    // across the canvas, z lifts it toward the reader. Canvas units; set by Canvas.ApplyPlacements.
    public float3 Placement { get; private set; }

    // How much the StackLayers above scale this chunk, about the pivots they name; folded into Placement.
    public float PlacementScale { get; private set; } = 1f;

    internal bool SetPlacement(in float3 placement, float scale = 1f)
    {
        if (Placement == placement && PlacementScale == scale)
            return false;
        Placement = placement;
        PlacementScale = scale;
        if (ChunkSlot != null && !ChunkSlot.IsDestroyed)
            ApplyFacing();
        return true;
    }

    public void SetComputeOffset(float2 offset)
    {
        if (ComputeOffset == offset && ChunkSlot != null && !ChunkSlot.IsDestroyed)
            return;
        ComputeOffset = offset;
        SetupComponents();
        ApplyFacing();
    }

    private static readonly floatQ HalfTurn = floatQ.AxisAngle(float3.Up, System.MathF.PI);

    // A canvas read from its slot's backward side (Canvas.FaceBackward) turns each chunk half a turn about the
    // slot's up axis; the render offset turns with it.
    internal void ApplyFacing()
    {
        if (ChunkSlot == null || ChunkSlot.IsDestroyed)
            return;
        bool backward = Canvas.FaceBackward.Value;
        var rotation = backward ? HalfTurn : floatQ.Identity;
        if (ChunkSlot.LocalRotation.Value != rotation)
            ChunkSlot.LocalRotation.Value = rotation;
        float k = PlacementScale;
        var position = Canvas.CanvasToLocal(ComputeOffset * k + new float2(Placement.x, Placement.y));
        if (Placement.z != 0f)
            position = new float3(position.x, position.y, backward ? -Placement.z : Placement.z);
        if (ChunkSlot.LocalPosition.Value != position)
            ChunkSlot.LocalPosition.Value = position;
        var size = k == 1f ? float3.One : new float3(k, k, 1f);
        if (ChunkSlot.LocalScale.Value != size)
            ChunkSlot.LocalScale.Value = size;
    }

    // 0 = normal nested chunk. > 0 = overlay layer: reserves a render-priority band above all
    // normal chunks so a modal draws on top of everything (see BuildPerSurfacePriorities). - xlinka
    public int OverlayLevel { get; set; }

    // Tree-order sort index, assigned by the Canvas in hierarchy order on each render-root cycle. Drives the
    // mesh renderer's SortingOrder (Godot sorting_offset), which is the INNER transparent-sort key under
    // render_priority. So this gives later-in-tree chunks a higher offset -> they tie-break ON TOP of earlier
    // ones that share a render_priority value; without it, same-band chunks would tie in an undefined
    // order. Stored on the chunk so a scoped re-mesh keeps its place. -xlinka
    public int OrderIndex { get; set; }

    private UIUnlitMaterial? _defaultMaterial;
    private MaterialCloneCache? _materialCloneCache;
    private float2 _lastClipOffset;
    private bool _hasClipOffset;

    public GraphicsChunk(Canvas canvas, RectTransform root)
    {
        Canvas = canvas;
        Root = root;
        ContentRenderData = new RenderData(this);
    }

    public void SetupComponents()
    {
        if (ChunkSlot != null)
            return;
        ChunkSlot = Canvas.Slot.AddLocalSlot("GraphicsChunk");
        if (Canvas.FaceBackward.Value || Placement != default || PlacementScale != 1f)
            ApplyFacing();
    }

    // Pin this chunk's clip window as it slides (render-offset scrolling): pushes the scroll offset into the
    // clip_offset uniform on every cloned material, so the fixed canvas-space clip rect clips the moved
    // content correctly. No mesh touched. -xlinka
    // persist=false: live scroll (cheap direct shader-param write). persist=true: after a structural rebuild,
    // also write the material Sync so its own update doesn't reset the offset. -xlinka
    public void SetClipOffset(float2 offset, bool persist)
    {
        // Skip the per-clone write when the offset is unchanged: a live scroll re-pushes to every material in
        // the chunk, so a momentum-settle frame or an idle repaint at the same position would churn one
        // SetShaderParameter per card for nothing. A rebuild (persist) always applies - the clones were just
        // re-created and need re-pinning to the current position. -xlinka
        if (!persist && _hasClipOffset && _lastClipOffset == offset)
            return;
        _lastClipOffset = offset;
        _hasClipOffset = true;
        _materialCloneCache?.SetClipOffset(offset, persist);
    }

    // Shared per-atlas/per-texture materials live on the CANVAS now: with per-row chunk roots (an
    // inspector panel has 70+), per-chunk caches duplicated every atlas material and its
    // render-priority clones once per row. One canvas-wide material per atlas/texture. - xlinka
    public UITextMaterial GetSharedTextMaterial(TextureAsset atlas) => Canvas.GetSharedTextMaterial(atlas);

    public MainTexturePropertyBlock GetSharedImageBlock(IAssetProvider<TextureAsset> texture) => Canvas.GetSharedImageBlock(texture);

    public UIUnlitMaterial GetSharedImageMaterial(IAssetProvider<TextureAsset> texture, bool still = false) => Canvas.GetSharedImageMaterial(texture, still);

    public IAssetProvider<MaterialAsset> GetDefaultUIMaterial()
    {
        if (Canvas.SharesMaterials && Canvas.World is { } world)
            return SharedUIMaterials.Of(world).Plain(Canvas.DrawsOverlay, Canvas.Culling.Value);
        SetupComponents();
        if (_defaultMaterial != null && !_defaultMaterial.IsDestroyed && _defaultMaterial.Culling.Value != Canvas.Culling.Value)
        {
            _defaultMaterial.Culling.Value = Canvas.Culling.Value;
            _defaultMaterial.ForceUpdate();
        }
        if (_defaultMaterial == null)
        {
            _defaultMaterial = ChunkSlot.GetComponent<UIUnlitMaterial>() ?? ChunkSlot.AttachComponent<UIUnlitMaterial>();
            _defaultMaterial.Culling.Value = Canvas.Culling.Value;
            _defaultMaterial.ZWrite.Value = ZWrite.Off;
            _defaultMaterial.RenderQueue.Value = 3000;
            if (Canvas.DrawsOverlay)
                _defaultMaterial.ZTest.Value = ZTest.Always;
        }
        return _defaultMaterial;
    }

    public void PrepareCompute()
    {
        _materialCloneCache?.BeginFrame();
        ContentRenderData.PrepareCompute();
    }

    public void SubmitChanges(int sortingOrder = 0)
    {
        OrderIndex = sortingOrder;
        ContentRenderData.SubmitChanges(sortingOrder);
        _materialCloneCache?.EndFrame();
    }

    // Push this chunk's tree-order index to its renderer WITHOUT re-meshing. Used on a render-root cycle for
    // chunks that were seen but not recomputed (their mesh is unchanged, but their place in tree order may have
    // shifted because a sibling chunk was added/removed). Cheap - just sets the SortingOrder sync. -xlinka
    public void ApplyOrderToRenderer()
    {
        if (MeshRenderer != null && !MeshRenderer.IsDestroyed)
            MeshRenderer.SortingOrder.Value = OrderIndex;
    }

    // Show/hide a built chunk without rebuilding it. Hiding content disables the chunk slot
    // (its mesh persists); re-showing re-enables it instantly, vs disposing + re-tessellating
    // which is what made re-shown content pop in a frame late.
    public void SetActive(bool active)
    {
        if (ChunkSlot != null && !ChunkSlot.IsDestroyed)
            ChunkSlot.ActiveSelf.Value = active;
    }

    // Tear down a nested chunk whose GraphicChunkRoot was removed from the tree. - xlinka
    public void Dispose()
    {
        _materialCloneCache?.Destroy();
        _materialCloneCache = null;
        ChunkSlot?.Destroy();
    }

    private IAssetProvider<MaterialAsset>? GetClippedMaterial(IAssetProvider<MaterialAsset>? source, in Rect clipRect)
    {
        if (source == null)
        {
            return null;
        }

        SetupComponents();
        _materialCloneCache ??= new MaterialCloneCache(ChunkSlot);
        return _materialCloneCache.GetClippedMaterial(source, clipRect);
    }

    // Clone of a UI material that swaps to the stencil-write or stencil-test shader variant (and folds in the
    // inherited rect clip). Used for shaped GPU masking; the rect-clip path above is untouched. -xlinka
    private IAssetProvider<MaterialAsset>? GetStencilMaterial(IAssetProvider<MaterialAsset>? source, StencilRole role, Rect? clipRect)
    {
        if (source == null)
        {
            return null;
        }

        SetupComponents();
        _materialCloneCache ??= new MaterialCloneCache(ChunkSlot);
        return _materialCloneCache.GetStencilMaterial(source, role, clipRect);
    }

    // Per-surface render priority requires its own cloned material because the
    // MeshRendererHook mutates Material.RenderPriority in place. Without cloning,
    // all surfaces sharing the source material collapse to whichever priority
    // was written last -> coplanar UI quads fall back to Godot's distance sort
    // and flip order as the camera rotates ("colors lost at angle"). - xlinka
    internal IAssetProvider<MaterialAsset>? GetRenderPriorityMaterial(IAssetProvider<MaterialAsset>? source, int surfaceIndex, int renderPriority)
    {
        if (source == null) return null;
        if (Canvas.SharesMaterials && Canvas.World is { } world)
            return SharedUIMaterials.Of(world).Priority(source, renderPriority);
        SetupComponents();
        _materialCloneCache ??= new MaterialCloneCache(ChunkSlot);
        return _materialCloneCache.GetRenderPriorityMaterial(source, surfaceIndex, renderPriority);
    }
}
