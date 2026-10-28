// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Godot;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using Lumora.Core.Components.Avatar;
using Lumora.Core.Phos;
using Lumora.Godot.Extensions;
using Lumora.Godot.Helpers;
using System.Collections.Generic;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

[ImplementableHook(typeof(SkinnedMeshRenderer))]
public class SkinnedMeshHook : ComponentHook<SkinnedMeshRenderer>, ILodRangeTarget
{
    // LOD plumbing. A LodGroup or LodDistanceCull decides the band; the single mesh instance this hook
    // owns is created once in Initialize and never replaced, so applying on receipt is enough. -xlinka
    private LodVisibilityRange _lodRange = LodVisibilityRange.Unbounded;

    private LodVisibilityRange _drawRange = LodVisibilityRange.Unbounded;
    private float _appliedDrawDistance;

    public void SetLodVisibilityRange(in LodVisibilityRange range)
    {
        if (_lodRange.Equals(range))
            return;
        _lodRange = range;
        ApplyRanges();
    }

    private LodVisibilityRange VisibleRange => LodVisibilityRange.Tightest(_lodRange, _drawRange);

    private void ApplyRanges()
    {
        var range = VisibleRange;
        range.ApplyTo(_meshInstance);
        if (_twin != null && _twin.Alive)
            range.ApplyTo(_twin.Instance);
    }

    private MeshInstance3D _meshInstance = null!;

    // The instance this renderer draws through, for a picture of the avatar it is on (AvatarPictures). Null
    // while it is not built and bound, or not meant to be drawn.
    internal MeshInstance3D? PictureSource
        => MeshInstanceAlive && _meshApplied && Owner.Enabled && (Owner.Slot?.IsActive ?? false) && !Owner.LocallyHiddenWhole
            && _meshInstance.Mesh != null ? _meshInstance : null;

    // Both the mesh instance and the skeleton are platform nodes that can be freed underneath us - by a
    // teardown, or by SkeletonHook rebuilding its skeleton - and a null check does NOT catch a freed
    // one. Every entry point revalidates instead of trusting the cached pointer. -xlinka
    private bool MeshInstanceAlive => _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance);
    private bool SkeletonAlive => _skeleton != null && GodotObject.IsInstanceValid(_skeleton) && !_skeleton.IsQueuedForDeletion();
    private ArrayMesh _arrayMesh = null!;

    // Which submesh each surface of _arrayMesh was built from. An empty submesh makes no surface, so the two
    // numberings part ways at the first one, and materials belong to submeshes. Null reads as one to one.
    private int[]? _surfaceSubmeshes;

    // RenderingServer refuses surface 257 and up (MAX_MESH_SURFACES), one engine error per rejected surface.
    private const int MaxGodotMeshSurfaces = 256;
    private SkeletonHook _skeletonHook = null!;
    private Skeleton3D _skeleton = null!;
    private bool _meshApplied;
    private bool _skeletonBound;
    private int _lastBindAttemptBoneCount = -1;
    // True once the real (component) material asset has been put on the surface. Stays false while we're showing
    // the neutral fallback, so the update loop keeps retrying until the PBS asset finishes loading. -xlinka
    private bool _realMaterialApplied;
    private bool _appliedSimple;

    // What the instance was last told to cast. A fresh MeshInstance3D casts, which is also the renderer's default.
    private GeometryInstance3D.ShadowCastingSetting _appliedShadow = GeometryInstance3D.ShadowCastingSetting.On;

    // Async mesh-build state: a worker-thread build is in flight (don't double-dispatch), plus the asset + vertex
    // count we last successfully applied so a lingering dirty flag can't trigger an endless rebuild loop. -xlinka
    private bool _meshBuildInFlight;
    private MeshDataAsset _appliedAsset = null!;
    private int _appliedMeshVcount = -1;
    private int _appliedHideVersion;
    private bool[]? _appliedHidden;

    // The same triangles left out as the last build, whether or not the set was handed in again since.
    private static bool SameHidden(bool[]? a, bool[]? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null || a.Length != b.Length)
            return false;
        return System.MemoryExtensions.SequenceEqual<bool>(a, b);
    }

    // Maps mesh bone index -> Godot skeleton bone index
    private Dictionary<int, int> _boneIndexMap = new Dictionary<int, int>();

    // EYE-VIEW TWIN. The zero-scale UserView overrides that want one of this mesh's bones collapsed for
    // the wearer, registered by LocalViewOverrideHook on its activation edges. While any are present
    // and the mesh is bound, _twin is the wearer's private headless copy (EyeViewTwin) and the real
    // instance sits on LOCAL_HEAD_LAYER so the eyes skip it. Only ever built for the local user's
    // worn avatar - the override hook never registers for anyone else's. -xlinka
    private readonly List<LocalViewOverride> _eyeCollapses = new List<LocalViewOverride>();
    private EyeViewTwin? _twin;
    // The ArrayMesh the twin instance currently holds. A mesh rebuild swaps _arrayMesh before the twin
    // is re-mirrored, and a morph weight or material written to the twin against the old mesh is an
    // index error per surface; this is the managed-side answer so the per-frame paths need no read.
    private ArrayMesh? _twinMesh;
    private uint _realLayersBeforeTwin;
    private bool _realLayersSwapped;

    public override void Initialize()
    {
        base.Initialize();

        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "SkinnedMeshInstance";

        // Added under the slot node first; TryBindToSkeleton reparents it under the Skeleton3D once that exists.
        attachedNode.AddChild(_meshInstance);
        LumoraLogger.Debug($"SkinnedMeshHook: Initialized and added mesh to '{Owner.Slot.SlotName.Value}'");

        TryBindToSkeleton();

        if (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null)
        {
            ApplyMesh();
        }
    }

    public override void ApplyChanges()
    {
        if (!MeshInstanceAlive)
            return;

        // Zero-mapping rebind only retries when the bone list actually changed. Retrying every
        // ApplyChanges made a renderer whose bones never map (face/eye mesh with foreign bone names)
        // rebind and FULLY REBUILD its mesh on every blendshape weight change - every blink flashed
        // the whole mesh. -xlinka
        bool zeroMapRetry = _skeletonBound && _boneIndexMap.Count == 0 && Owner.BoneNames.Count > 0
            && Owner.BoneNames.Count != _lastBindAttemptBoneCount;

        // A skeleton reference can change during streamed arrival. Refresh bindings on its ready
        // signal too: rebuilding the bone table preserves the node but can change every bone index.
        bool skeletonStale = _skeletonBound && !SkeletonAlive;
        bool skeletonChanged = Owner.SkeletonChanged;
        Owner.SkeletonChanged = false;
        if (skeletonStale || skeletonChanged)
        {
            DestroyEyeViewTwin(logIt: false);
            _skeletonBound = false;
            _skeleton = null!;
            _skeletonHook = null!;
            _boneIndexMap.Clear();
            Owner.HookBindingComplete = false;
        }

        if (!_skeletonBound || zeroMapRetry)
        {
            TryBindToSkeleton();
        }

        // Consume the flag. Nothing cleared it before, so `shouldApplyMesh` was true on EVERY pass for
        // the rest of the session - harmless while ApplyChanges ran rarely, and a full mesh rebuild per
        // frame the moment anything started dirtying this hook regularly. -xlinka
        bool shouldApplyMesh = Owner.MeshDataChanged || !_meshApplied;
        Owner.MeshDataChanged = false;
        if (shouldApplyMesh && (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null))
        {
            ApplyMesh();
        }

        if (GodotObject.IsInstanceValid(_meshInstance))
        {
            // SLOT ACTIVE HAS TO BE ASKED FOR HERE, unlike every unskinned renderer.
            //
            // A slot hides by hiding its own Node3D and letting Godot's visibility cascade take the
            // rest of the subtree with it. That works right up until TryBindToSkeleton REPARENTS this
            // mesh instance under the Skeleton3D, which lives under the armature and not under this
            // renderer's slot. From then on the slot hides an empty node while the mesh carries on
            // drawing from under the skeleton - which is why switching a part off in the inspector
            // did nothing on an avatar and worked fine on everything else. -xlinka
            bool visible = Owner.Enabled && (Owner.Slot?.IsActive ?? false) && !Owner.LocallyHiddenWhole;
            _meshInstance.Visible = visible;
            if (_twin != null && _twin.Alive)
                _twin.Instance.Visible = visible;

            // The renderer's cast setting never reached the instance, so every skinned mesh cast a shadow
            // whatever it was set to, and switching an avatar's shadows off saved nothing. The twin stays
            // off either way; the real instance is the one that casts for the wearer. -xlinka
            var shadow = Owner.LocalShadowCulled ? GeometryInstance3D.ShadowCastingSetting.Off : ShadowSetting(Owner.ShadowCastMode.Value);
            if (shadow != _appliedShadow)
            {
                _meshInstance.CastShadow = shadow;
                _appliedShadow = shadow;
            }

            // Someone else's avatar is not drawn past the viewer's avatar draw distance, inside whatever band
            // a LOD group gives it. A hard cut with a few metres of give: a fade would turn every material on
            // the body see-through on its way out. -xlinka
            float draw = Owner.LocalDrawDistance;
            if (draw != _appliedDrawDistance)
            {
                _appliedDrawDistance = draw;
                _drawRange = draw > 0f ? new LodVisibilityRange(0f, 0f, draw, System.MathF.Max(2f, draw * 0.05f), false) : LodVisibilityRange.Unbounded;
                ApplyRanges();
            }
        }

        // Update material if changed (target reassigned), or keep retrying while the real asset is still loading
        // so the neutral fallback gets replaced the moment the PBS material is valid (MAT-3 race fix). -xlinka
        bool materialsChanged = Owner.MaterialsChanged;
        Owner.MaterialsChanged = false;
        if (Owner.LocalSimpleShading != _appliedSimple)
        {
            _appliedSimple = Owner.LocalSimpleShading;
            materialsChanged = true;
        }
        if (materialsChanged | Owner.Material.GetWasChangedAndClear())
        {
            _realMaterialApplied = false;
            ApplyMaterial();
        }
        else if (_meshApplied && !_realMaterialApplied)
        {
            ApplyMaterial();
        }

        // Keep asking until the real material lands, WHICHEVER branch just painted the placeholder.
        //
        // ApplyChanges is QUEUE-driven, not per-frame: it runs when something dirties this hook and not
        // otherwise. A texture finishing its download dirties nothing here: the material re-binds itself
        // (its texture refs are direct members, so their arrival marks it dirty), but the renderer holds
        // that material in the Materials LIST, and a list element's change event has no listener - all
        // the arrival does on the renderer is set MaterialsChanged, which nobody reads until the hook
        // runs again. So every path that can leave a surface on the checker has to re-queue, not just the
        // retry branch above. It used to be only that branch: a surface that went on the placeholder
        // from the materials-changed branch, or from the mesh finalize, was stuck with no retry and no
        // "stuck" warning (the watch fires only on a repeat visit). On the dog import that was the
        // Fluff mane: its mesh finalized while its 4096-square albedo was still decoding, went on the
        // checker, and nothing ever came back for it. The body, same material type and same texture
        // format, escaped - most likely because its morph build (17k verts x 307 shapes against the
        // mane's 8k x 23) finalizes so much later that the texture is already there by then. It is a
        // race either way, and which surfaces lose it changes with the machine.
        //
        // Re-queueing while a surface is still on the placeholder costs one pass per hook per frame for
        // as long as the load takes, and stops the moment every surface is latched. -xlinka
        RequeueWhilePlaceholderShown();

        // Cheap path: blendshape weights changed (blink/viseme/expression) - reapply, no rebuild.
        if (Owner.BlendWeightsChanged)
        {
            ApplyBlendShapeWeights(incremental: true);
            Owner.BlendWeightsChanged = false;
        }
    }

    private static GeometryInstance3D.ShadowCastingSetting ShadowSetting(ShadowCastMode mode) => mode switch
    {
        ShadowCastMode.Off => GeometryInstance3D.ShadowCastingSetting.Off,
        ShadowCastMode.ShadowOnly => GeometryInstance3D.ShadowCastingSetting.ShadowsOnly,
        ShadowCastMode.DoubleSided => GeometryInstance3D.ShadowCastingSetting.DoubleSided,
        _ => GeometryInstance3D.ShadowCastingSetting.On,
    };

    // What each weight was last set to, for the per-frame path. A blink moves two weights of three
    // hundred and every one of them used to go across to the platform on every change. Anything that
    // swaps the mesh, the instance or the twin writes the whole set again. -xlinka
    private float[] _appliedWeights = System.Array.Empty<float>();
    private ArrayMesh? _weightsMesh;
    private MeshInstance3D? _weightsInstance;
    private MeshInstance3D? _weightsTwin;

    private void ApplyBlendShapeWeights(bool incremental = false)
    {
        if (_meshInstance == null || !GodotObject.IsInstanceValid(_meshInstance) || _arrayMesh == null)
            return;

        // Bound by the count the RENDERING SERVER actually allocated for this mesh (mesh_get_blend_shape_count on
        // the RID) - which is exactly what the mesh instance's blend_weights is sized to - NOT the resource-level
        // GetBlendShapeCount(). They disagree when a surface registered blend-shape NAMES but failed to attach the
        // blend-shape DATA: the resource reports 33, the instance has 0. Writing a weight past the real count
        // spams "Index out of bounds" errors, each capturing a managed backtrace - and since animated weights
        // (blink/visemes) re-apply every frame, that flood froze GLB avatar imports for ~10s. -xlinka
        int n = (int)RenderingServer.MeshGetBlendShapeCount(_arrayMesh.GetRid());
        if (n <= 0)
            return;

        int weights = Owner.BlendShapeNames.Count;
        var twin = _twin != null && _twin.Alive && ReferenceEquals(_twinMesh, _arrayMesh) ? _twin.Instance : null;
        bool full = !incremental || _appliedWeights.Length != n || !ReferenceEquals(_weightsMesh, _arrayMesh)
            || !ReferenceEquals(_weightsInstance, _meshInstance) || !ReferenceEquals(_weightsTwin, twin);
        if (full)
        {
            if (_appliedWeights.Length != n)
                _appliedWeights = new float[n];
            _weightsMesh = _arrayMesh;
            _weightsInstance = _meshInstance;
            _weightsTwin = twin;
        }
        for (int i = 0; i < n; i++)
        {
            float weight = i < weights ? Owner.GetEffectiveBlendShapeWeight(i) : 0f;
            if (!full && _appliedWeights[i] == weight)
                continue;
            _appliedWeights[i] = weight;
            _meshInstance.SetBlendShapeValue(i, weight);
            twin?.SetBlendShapeValue(i, weight);
        }
    }

    // Every surface gets its own material now. Painting only surface 0 left a multi-surface mesh -
    // a body with a separate face or eyes - showing one material where it should show several. -xlinka
    private void ApplyMaterial()
    {
        if (!MeshInstanceAlive || _arrayMesh == null) return;

        int surfaces = _arrayMesh.GetSurfaceCount();
        if (surfaces <= 0) return;

        bool allApplied = true;
        for (int surface = 0; surface < surfaces; surface++)
            allApplied &= ApplySurfaceMaterial(surface);

        _realMaterialApplied = allApplied;
    }

    // True once this surface is wearing its final material and needs no further retry.
    private bool ApplySurfaceMaterial(int surface)
    {
        int index = SubmeshOf(surface);
        var provider = Owner.MaterialFor(index);
        bool loading = Owner.IsSurfaceLoading(index);

        // Nothing assigned is AUTHORED, not loading: no override, no loading skin, and latched so the
        // retry loop stops asking. It used to paint a flat tan guess here, which is a claim about a
        // material that does not exist. -xlinka
        if (provider == null && !loading)
        {
            SetSurfaceMaterial(surface, null);
            return true;
        }

        var materialAsset = provider?.Asset;
        if (!loading && materialAsset != null && materialAsset.GodotMaterial is Material godotMaterial)
        {
            if (_appliedSimple && provider is PoiyomiMaterial toon)
                godotMaterial = AvatarFallbackMaterial.For(toon);
            SetSurfaceMaterial(surface, godotMaterial);
            _loadingSkin.Clear(index);
            return true;
        }

        _loadingSkin.Note(index, provider, Owner.Slot?.SlotName.Value);

        // Assigned but still arriving (the material asset, or its textures). Wear the shared loading
        // skin and DON'T latch it - leave the flag false so the arrival notification's re-drive swaps
        // in the real material. An avatar used to get stuck in the tan. -xlinka
        SetSurfaceMaterial(surface, LoadingPlaceholderMaterial.Get());
        return false;
    }

    private int SubmeshOf(int surface) =>
        _surfaceSubmeshes != null && surface < _surfaceSubmeshes.Length ? _surfaceSubmeshes[surface] : surface;

    // Same material again is a no-op here: setting it re-files the instance and re-renders the shadow of
    // every light around the body (see MeshRendererHookBase.ApplyMaterials).
    private void SetSurfaceMaterial(int surface, Material? material)
    {
        if (!ReferenceEquals(_meshInstance!.GetSurfaceOverrideMaterial(surface), material))
            _meshInstance.SetSurfaceOverrideMaterial(surface, material);
        if (_twin != null && _twin.Alive && ReferenceEquals(_twinMesh, _arrayMesh)
            && !ReferenceEquals(_twin.Instance.GetSurfaceOverrideMaterial(surface), material))
            _twin.Instance.SetSurfaceOverrideMaterial(surface, material);
    }

    private readonly LoadingSkinWatch _loadingSkin = new();

    // One more ApplyChanges next frame while any surface wears the loading skin. Safe from the deferred
    // mesh finalize too: the datamodel write goes through the world's synchronous queue rather than
    // straight into the change buckets from a Godot callback, the same route the blendshape-name mirror
    // in that finalize already takes. A no-op once every surface holds its real material. -xlinka
    private void RequeueWhilePlaceholderShown()
    {
        if (_realMaterialApplied || !_meshApplied)
            return;

        var owner = Owner;
        if (owner == null || owner.IsDestroyed)
            return;

        var world = owner.World;
        if (world == null)
        {
            owner.MarkChangeDirty();
            return;
        }

        world.RunSynchronously(() =>
        {
            if (!owner.IsDestroyed)
                owner.MarkChangeDirty();
        });
    }

    private void TryBindToSkeleton()
    {
        // Called from the stale-skeleton path too, by which point the instance itself may be gone.
        if (!MeshInstanceAlive)
            return;

        if (Owner.Skeleton.Target != null)
        {
            _skeletonHook = (Owner.Skeleton.Target.Hook as SkeletonHook)!;
            if (_skeletonHook != null)
            {
                _skeleton = _skeletonHook.GetSkeleton();
            }
        }

        // No explicit skeleton reference: walk up from the first bone slot's node to find the Skeleton3D.
        if (_skeleton == null && Owner.Bones.Count > 0 && Owner.Bones[0] != null)
        {
            var boneSlot = Owner.Bones[0]!;
            var slotHook = boneSlot.Hook as SlotHook;
            if (slotHook != null)
            {
                var node = slotHook.GeneratedNode3D;
                if (node != null)
                {
                    var parent = node.GetParent();
                    while (parent != null)
                    {
                        if (parent is Skeleton3D skel)
                        {
                            _skeleton = skel;
                            break;
                        }
                        parent = parent.GetParent();
                    }
                }
            }
        }

        if (!SkeletonAlive)
        {
            if (_meshInstance.GetParent() == null)
            {
                attachedNode.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: No skeleton found - added mesh as static");
            }
            return;
        }

        // SkeletonAlive already proved this non-null; the analyser cannot see through the property.
        if (_skeleton!.GetBoneCount() == 0)
        {
            if (_meshInstance.GetParent() == null)
            {
                attachedNode.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: Skeleton has no bones - added mesh as static");
            }
            return;
        }

        BuildBoneIndexMap();

        if (_meshInstance.GetParent() != null)
        {
            if (_meshInstance.GetParent() != _skeleton)
            {
                _meshInstance.GetParent().RemoveChild(_meshInstance);
                _skeleton.AddChild(_meshInstance);
                LumoraLogger.Debug("SkinnedMeshHook: Reparented mesh under skeleton");
            }
        }
        else
        {
            _skeleton.AddChild(_meshInstance);
            LumoraLogger.Debug("SkinnedMeshHook: Added mesh as child of skeleton");
        }

        // IDENTITY, and not the transform this instance carried under its own slot. A skinned vertex
        // lands at skeleton * skinMatrix * vertex, and the skin's inverse binds already carry mesh
        // space -> bone space, so anything left on the instance is applied a SECOND time and slides
        // the body off its armature. This used to preserve the global transform across the reparent,
        // which is exactly that bug: any model whose mesh node carries an offset - most Blender and
        // FBX exports, where the mesh hangs under an Armature node with its own transform - rendered
        // its mesh away from its bones. A skinned mesh node's own transform is ignored by definition;
        // the armature places the mesh, not the node it was authored under. -xlinka
        _meshInstance.Transform = Transform3D.Identity;

        // ".." because the mesh was just parented under the skeleton.
        _meshInstance.Skeleton = new NodePath("..");

        _skeletonBound = true;
        _lastBindAttemptBoneCount = Owner.BoneNames.Count;
        LumoraLogger.Debug($"SkinnedMeshHook: Bound to skeleton '{_skeleton.Name}' with {_skeleton.GetBoneCount()} bones, mapped {_boneIndexMap.Count} mesh bones");

        // Re-apply mesh now that we have skeleton with proper bone mapping / Skin. Skip when nothing
        // mapped (a rigid mesh renders fine without a Skin) - rebuilding then is churn for no gain.
        if ((_boneIndexMap.Count > 0 || Owner.BoneNames.Count == 0)
            && (Owner.Vertices.Count > 0 || Owner.MeshAsset.Target != null))
        {
            var asset = Owner.MeshAsset.Asset;
            if (_meshApplied && asset != null && ReferenceEquals(asset, _appliedAsset))
            {
                // Asset vertices use mesh-bone indices. A new skeleton only needs a new Skin;
                // keep the existing geometry visible instead of decoding/uploading it again.
                BuildAndAssignSkinFromAsset(asset);
            }
            else
            {
                _meshApplied = false;
                ApplyMesh();
            }
        }

        // Stops the component's update-loop re-drive polling.
        if (_boneIndexMap.Count > 0)
        {
            Owner.HookBindingComplete = true;
        }

        RefreshEyeViewTwin();
    }

    // BONES ARE LOOKED UP BY THE DATAMODEL'S NAMES.
    //
    // A bind list names bones the way the datamodel does, and the skeleton does not always carry that name:
    // Godot refuses some, and the skeleton hook gives those bones another (SkeletonHook.GodotBoneName). So a
    // name goes through the hook that built the skeleton, which knows both, and a bone slot is found by the
    // slot itself before its name is tried. Without our own hook the skeleton is asked for the name Godot
    // would have been given. -xlinka
    private SkeletonHook? OwnSkeletonHook
        => _skeletonHook != null && _skeleton != null && ReferenceEquals(_skeletonHook.GetSkeleton(), _skeleton) ? _skeletonHook : null;

    private int FindSkeletonBone(string? boneName)
    {
        if (string.IsNullOrEmpty(boneName) || _skeleton == null)
            return -1;
        if (OwnSkeletonHook is { } hook)
            return hook.GetBoneIndex(boneName);
        return _skeleton.FindBone(SkeletonHook.GodotBoneName(boneName));
    }

    private int FindSkeletonBone(Lumora.Core.Slot? boneSlot, string? boneName)
    {
        if (boneSlot != null && OwnSkeletonHook is { } hook && hook.GetBoneIndex(boneSlot) is int index and >= 0)
            return index;
        return FindSkeletonBone(boneName);
    }

    // The name a bind list knows a skeleton bone by: the skeleton's own, or the datamodel's where Godot
    // made the skeleton carry another.
    private string BindNameOf(int bone)
    {
        string skeletonName = _skeleton.GetBoneName(bone);
        if (OwnSkeletonHook?.GetBoneSourceName(bone) is { } source && SkeletonHook.GodotBoneName(source) != source)
            return source;
        return skeletonName;
    }

    private void BuildBoneIndexMap()
    {
        _boneIndexMap.Clear();

        if (_skeleton == null)
            return;

        // Mapping priority: BoneNames, then Bones slot refs, then the SkeletonBuilder's order, else 1:1.
        if (Owner.BoneNames.Count > 0)
        {
            int missing = 0;
            string firstMissing = string.Empty;
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.BoneNames.Count; meshBoneIdx++)
            {
                string boneName = Owner.BoneNames[meshBoneIdx];
                int skelBoneIdx = FindSkeletonBone(boneName);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
                else
                {
                    _boneIndexMap[meshBoneIdx] = 0;
                    if (missing++ == 0)
                        firstMissing = boneName;
                }
            }
            if (missing > 0)
                LumoraLogger.Warn($"SkinnedMeshHook: {missing} of {Owner.BoneNames.Count} bones not in the skeleton (first '{firstMissing}'), mapped to bone 0");
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from names - {_boneIndexMap.Count} mappings");
            return;
        }

        if (Owner.Bones.Count > 0)
        {
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.Bones.Count; meshBoneIdx++)
            {
                var boneSlot = Owner.Bones[meshBoneIdx];
                if (boneSlot != null)
                {
                    int skelBoneIdx = FindSkeletonBone(boneSlot, boneSlot.SlotName.Value);

                    if (skelBoneIdx >= 0)
                    {
                        _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                    }
                    else
                    {
                        _boneIndexMap[meshBoneIdx] = 0;
                    }
                }
                else
                {
                    _boneIndexMap[meshBoneIdx] = 0;
                }
            }
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from slots - {_boneIndexMap.Count} mappings");
            return;
        }

        if (Owner.Skeleton.Target != null && Owner.Skeleton.Target.BoneNames.Count > 0)
        {
            var skelBuilder = Owner.Skeleton.Target;
            for (int meshBoneIdx = 0; meshBoneIdx < skelBuilder.BoneNames.Count; meshBoneIdx++)
            {
                var boneSlot = meshBoneIdx < skelBuilder.BoneSlots.Count ? skelBuilder.BoneSlots[meshBoneIdx] : null;
                int skelBoneIdx = FindSkeletonBone(boneSlot, skelBuilder.BoneNames[meshBoneIdx]);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
            }
            LumoraLogger.Debug($"SkinnedMeshHook: Built bone map from SkeletonBuilder - {_boneIndexMap.Count} mappings");
            return;
        }

        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _boneIndexMap[i] = i;
        }
        LumoraLogger.Debug($"SkinnedMeshHook: Using direct bone index mapping (fallback)");
    }

    // returns 0 for invalid indices to prevent Godot errors
    private int RemapBoneIndex(int meshBoneIndex)
    {
        if (meshBoneIndex < 0)
            return 0;

        if (_boneIndexMap.TryGetValue(meshBoneIndex, out int skelBoneIndex))
        {
            int boneCount = _skeleton?.GetBoneCount() ?? 0;
            if (skelBoneIndex < 0 || skelBoneIndex >= boneCount)
                return 0;
            return skelBoneIndex;
        }

        int maxBone = (_skeleton?.GetBoneCount() ?? 1) - 1;
        return System.Math.Clamp(meshBoneIndex, 0, System.Math.Max(0, maxBone));
    }

    private void ApplyMesh()
    {
        if (!MeshInstanceAlive)
            return;

        // A full mesh rebuild re-uploads the ArrayMesh and re-registers the instance with lights/shadows,
        // which reads as a visible shadow/light pop on the whole avatar. Rebuilds should be RARE (import,
        // mesh data change, skeleton rebind) - if this line spams in the log while an avatar blinks or
        // idles, something upstream is tripping MeshDataChanged per frame and that's the bug. - xlinka
        LumoraLogger.Debug($"SkinnedMeshHook: full mesh rebuild on '{Owner.Slot?.SlotName.Value}'");

        // Phos asset path (the universal pipeline): geometry + bone bindings + bind poses come from a
        // content-hashed MeshDataAsset, and skinning is driven by an explicit Skin. Takes precedence over the
        // inline lists (which serve the legacy Godot-glTF import path). -xlinka
        // AssetRef.Asset returns the provider's loaded MeshDataAsset (null until the async decode lands). The
        // AssetRef is what reference-counts the provider so this load actually happens at all. -xlinka
        var phosAsset = Owner.MeshAsset.Asset;
        if (phosAsset?.MeshData is { VertexCount: > 0 } phosMesh)
        {
            ApplyMeshFromAsset(phosMesh, phosAsset);
            return;
        }

        if (!Owner.MeshAsset.Value.IsNull)
        {
            // A replacement asset is still arriving. Keep the previous geometry on screen and
            // let the asset-arrival callback replace it, rather than flashing an empty instance.
            _meshApplied = false;
            Owner.HookBindingComplete = false;
            return;
        }

        if (Owner.Vertices.Count == 0 || Owner.Indices.Count == 0)
        {
            LumoraLogger.Debug("SkinnedMeshHook: No mesh data");
            _meshInstance.Mesh = null;
            _meshApplied = false;
            return;
        }

        _arrayMesh?.Dispose();
        _arrayMesh = new ArrayMesh();
        _surfaceSubmeshes = null;

        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        // Vertices
        var vertices = new Vector3[Owner.Vertices.Count];
        for (int i = 0; i < Owner.Vertices.Count; i++)
        {
            var v = Owner.Vertices[i];
            vertices[i] = new Vector3(v.x, v.y, v.z);
        }
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;

        // Normals
        Vector3[]? baseNormals = null;
        if (Owner.Normals.Count == Owner.Vertices.Count)
        {
            baseNormals = new Vector3[Owner.Normals.Count];
            for (int i = 0; i < Owner.Normals.Count; i++)
            {
                var n = Owner.Normals[i];
                baseNormals[i] = new Vector3(n.x, n.y, n.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = baseNormals;
        }

        // UVs
        Vector2[]? baseUVs = null;
        if (Owner.UVs.Count == Owner.Vertices.Count)
        {
            baseUVs = new Vector2[Owner.UVs.Count];
            for (int i = 0; i < Owner.UVs.Count; i++)
            {
                var uv = Owner.UVs[i];
                baseUVs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = baseUVs;
        }

        // Indices
        var indices = new int[Owner.Indices.Count];
        int minIndex = int.MaxValue;
        int maxIndex = -1;
        for (int i = 0; i < Owner.Indices.Count; i++)
        {
            int index = Owner.Indices[i];
            if (index > maxIndex) maxIndex = index;
            if (index < minIndex) minIndex = index;
            indices[i] = index;
        }

        // An index outside the vertex buffer is an out-of-bounds read at draw time -
        // that's a Vulkan device-lost, not a visual glitch. Same guard as MeshHook. - xlinka
        if (Owner.Indices.Count > 0 && (maxIndex >= Owner.Vertices.Count || minIndex < 0))
        {
            LumoraLogger.Error($"SkinnedMeshHook: Index range [{minIndex}, {maxIndex}] invalid for vertex count {Owner.Vertices.Count} on slot '{Owner.Slot?.SlotName?.Value}' - surface skipped");
            arrays.Dispose();
            return;
        }
        arrays[(int)Mesh.ArrayType.Index] = indices;

        // Bone data only goes on the surface once the skeleton is ready with real mappings - Godot
        // errors on bone indices that don't exist yet.
        bool hasBoneData = Owner.BoneIndices.Count == Owner.Vertices.Count &&
                           Owner.BoneWeights.Count == Owner.Vertices.Count;
        bool hasValidMappings = _boneIndexMap.Count > 0 && SkeletonAlive && _skeleton.GetBoneCount() > 0;

        if (hasBoneData && hasValidMappings)
        {
            // Godot wants 4 bone indices per vertex, in SKELETON bone space (hence the remap).
            var boneIndices = new int[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneIndices.Count; i++)
            {
                var bi = Owner.BoneIndices[i];
                boneIndices[i * 4 + 0] = RemapBoneIndex(bi.x);
                boneIndices[i * 4 + 1] = RemapBoneIndex(bi.y);
                boneIndices[i * 4 + 2] = RemapBoneIndex(bi.z);
                boneIndices[i * 4 + 3] = RemapBoneIndex(bi.w);
            }
            arrays[(int)Mesh.ArrayType.Bones] = boneIndices;

            var boneWeights = new float[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneWeights.Count; i++)
            {
                var bw = Owner.BoneWeights[i];
                boneWeights[i * 4 + 0] = bw.x;
                boneWeights[i * 4 + 1] = bw.y;
                boneWeights[i * 4 + 2] = bw.z;
                boneWeights[i * 4 + 3] = bw.w;
            }
            arrays[(int)Mesh.ArrayType.Weights] = boneWeights;

            LumoraLogger.Debug($"SkinnedMeshHook: Added bone data for {Owner.Vertices.Count} vertices with {_boneIndexMap.Count} bone mappings");
        }
        else if (hasBoneData)
        {
            LumoraLogger.Debug($"SkinnedMeshHook: Skipping bone data - skeleton not ready (mappings={_boneIndexMap.Count}, skelBones={_skeleton?.GetBoneCount() ?? 0})");
        }
        else
        {
            LumoraLogger.Warn($"SkinnedMeshHook: Missing bone data - indices:{Owner.BoneIndices.Count} weights:{Owner.BoneWeights.Count} verts:{Owner.Vertices.Count}");
        }

        // Blendshapes: declare them on the mesh first, then hand the per-shape vertex arrays to the
        // surface. Stored exactly as the source reported them, with the source's BlendShapeMode, so
        // the morph round-trips correctly. (Only vertex deltas are carried; normals aren't morphed.)
        int vertexCount = Owner.Vertices.Count;
        int shapeCount = Owner.BlendShapeNames.Count;
        bool hasBlendShapes = shapeCount > 0 && Owner.BlendShapeVertices.Count == shapeCount * vertexCount;
        global::Godot.Collections.Array<global::Godot.Collections.Array> blendShapes = null!;
        var shapeArrayPool = new List<global::Godot.Collections.Array>();
        if (hasBlendShapes)
        {
            _arrayMesh.ClearBlendShapes();
            _arrayMesh.BlendShapeMode = (Mesh.BlendShapeMode)Owner.BlendShapeMode.Value;
            for (int s = 0; s < shapeCount; s++)
                _arrayMesh.AddBlendShape(Owner.BlendShapeNames[s]);

            // Godot 4 requires every blend shape to carry the SAME morphable attributes as the base surface for
            // VERTEX/NORMAL/TANGENT. The base here has NORMAL, so each shape must include a NORMAL array too or
            // the whole surface is rejected (the mesh vanishes). "No change" = the BASE normal in EVERY mode,
            // never a zero vector - zeros octahedral-encode to NaN garbage that gets blended in by weight and
            // the whole mesh's lighting pulses with every blink. See the identical fix in BuildArrayMeshCore
            // for the full autopsy; that bug ate days. base + w*base renormalizes to the same direction, so
            // the base array is safe in Relative mode too. -xlinka
            bool baseHasNormals = Owner.Normals.Count == vertexCount;
            // Use the source's real NORMAL morph deltas when the import carried them (so lighting follows the
            // expression); otherwise fall back to "no normal morph" values just to satisfy Godot's format
            // requirement. -xlinka
            bool hasMorphNormals = Owner.HasBlendShapeNormals;

            blendShapes = new global::Godot.Collections.Array<global::Godot.Collections.Array>();

            // One staging buffer per stream, reused by every shape: the assignment into shapeArrays
            // marshals a copy, and both loops overwrite every element. -xlinka
            var sv = new Vector3[vertexCount];
            var sn = baseHasNormals ? new Vector3[vertexCount] : null;

            for (int s = 0; s < shapeCount; s++)
            {
                var shapeArrays = new global::Godot.Collections.Array();
                shapeArrays.Resize((int)Mesh.ArrayType.Max);
                shapeArrayPool.Add(shapeArrays);
                int baseIdx = s * vertexCount;
                for (int i = 0; i < vertexCount; i++)
                {
                    var v = Owner.BlendShapeVertices[baseIdx + i];
                    sv[i] = new Vector3(v.x, v.y, v.z);
                }
                shapeArrays[(int)Mesh.ArrayType.Vertex] = sv;

                if (sn != null)
                {
                    for (int i = 0; i < vertexCount; i++)
                    {
                        if (hasMorphNormals)
                        {
                            var nd = Owner.BlendShapeNormals[baseIdx + i];
                            sn[i] = new Vector3(nd.x, nd.y, nd.z);
                        }
                        else
                        {
                            var nrm = Owner.Normals[i];
                            sn[i] = new Vector3(nrm.x, nrm.y, nrm.z);
                        }
                    }
                    shapeArrays[(int)Mesh.ArrayType.Normal] = sn;
                }

                blendShapes.Add(shapeArrays);
            }
        }

        // Add surface to mesh. CRITICAL: if the blend-shape surface add is rejected (Godot 4 wants each blend
        // shape's arrays to match the base surface's VERTEX/NORMAL/TANGENT set, but we only carry vertex deltas),
        // it adds NO surface at all - the whole mesh vanishes (the "main mesh missing" import bug). So detect the
        // drop via the surface count and fall back to a plain surface, so the body always renders even if the
        // morphs can't be attached. -xlinka
        if (hasBlendShapes)
        {
            int beforeSurfaces = _arrayMesh.GetSurfaceCount();
            _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, blendShapes, NoLods);
            if (_arrayMesh.GetSurfaceCount() == beforeSurfaces)
            {
                LumoraLogger.Warn($"SkinnedMeshHook: blend-shape surface rejected on slot '{Owner.Slot?.SlotName?.Value}' ({shapeCount} shapes) - rendering without morphs so the mesh still shows.");
                _arrayMesh.ClearBlendShapes();
                _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, NoLods);
            }
        }
        else
        {
            _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, NoLods);
        }

        // The surface holds its own copies; these only hand back their native storage when told to.
        for (int i = 0; i < shapeArrayPool.Count; i++)
            shapeArrayPool[i].Dispose();
        // Array<T> is a typed view over an untyped Array; the untyped one is what owns the storage.
        if (blendShapes != null)
            ((global::Godot.Collections.Array)blendShapes).Dispose();
        arrays.Dispose();

        _meshInstance.Mesh = _arrayMesh;
        ApplyBlendShapeWeights();

        // Apply material (real asset if loaded, else a non-latching fallback the update loop will replace).
        ApplyMaterial();

        _meshApplied = true;
        RefreshEyeViewTwin();
        RequeueWhilePlaceholderShown();
        LumoraLogger.Debug($"SkinnedMeshHook: Applied mesh with {Owner.Vertices.Count} vertices, {Owner.Indices.Count / 3} triangles");
    }

    // Per-vertex tangents (xyz + handedness w) via the standard accumulate-per-triangle + Gram-Schmidt method.
    // Needed because the synced skinned-mesh path doesn't carry tangents, so normal-mapped materials otherwise
    // render with a garbage tangent basis (speckled artifacts on detailed areas). -xlinka
    private static float[] ComputeTangents(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices)
    {
        int n = positions.Length;
        var tan = new Vector3[n];
        var bitan = new Vector3[n];

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= n || i1 >= n || i2 >= n)
                continue;

            Vector3 e1 = positions[i1] - positions[i0];
            Vector3 e2 = positions[i2] - positions[i0];
            Vector2 d1 = uvs[i1] - uvs[i0];
            Vector2 d2 = uvs[i2] - uvs[i0];

            float denom = d1.X * d2.Y - d2.X * d1.Y;
            float r = System.Math.Abs(denom) < 1e-12f ? 0f : 1f / denom;

            Vector3 sdir = new Vector3(
                (d2.Y * e1.X - d1.Y * e2.X) * r,
                (d2.Y * e1.Y - d1.Y * e2.Y) * r,
                (d2.Y * e1.Z - d1.Y * e2.Z) * r);
            Vector3 tdir = new Vector3(
                (d1.X * e2.X - d2.X * e1.X) * r,
                (d1.X * e2.Y - d2.X * e1.Y) * r,
                (d1.X * e2.Z - d2.X * e1.Z) * r);

            tan[i0] += sdir; tan[i1] += sdir; tan[i2] += sdir;
            bitan[i0] += tdir; bitan[i1] += tdir; bitan[i2] += tdir;
        }

        var result = new float[n * 4];
        for (int i = 0; i < n; i++)
        {
            Vector3 nv = normals[i];
            Vector3 tv = tan[i];
            // Gram-Schmidt: make the tangent orthogonal to the normal.
            Vector3 ortho = tv - nv * nv.Dot(tv);
            ortho = ortho.LengthSquared() > 1e-12f ? ortho.Normalized() : new Vector3(1f, 0f, 0f);
            float w = nv.Cross(tv).Dot(bitan[i]) < 0f ? -1f : 1f;
            result[i * 4 + 0] = ortho.X;
            result[i * 4 + 1] = ortho.Y;
            result[i * 4 + 2] = ortho.Z;
            result[i * 4 + 3] = w;
        }
        return result;
    }

    // Build the Godot skinned mesh straight from the Phos asset: positions/normals/uvs/indices + the raw
    // per-vertex bone indices (kept in MESH-bone space) + weights, then an explicit Skin that maps each mesh
    // bone to its skeleton bone with the asset's bind pose. The Skin is why the mesh holds its shape when the
    // rig poses - without it Godot skins from rest poses and the mesh collapses. Needs the skeleton built first;
    // returns quietly (leaving _meshApplied false) until then so the update loop re-enters. -xlinka
    private void ApplyMeshFromAsset(PhosMesh mesh, MeshDataAsset asset)
    {
        // IsInstanceValid before ANY call on it - a null check alone does not cover a disposed node.
        if (!MeshInstanceAlive)
        {
            _meshApplied = false;
            return;
        }

        // A skeleton is only required if the MESH IS ACTUALLY SKINNED. Content routinely puts an
        // unskinned mesh on a skinned renderer - a swappable alternate body, a toggled accessory - and
        // demanding a skeleton for those meant they rendered nothing at all, forever, with no error.
        // With no bone data there is nothing to skin: build it and let it ride the slot transform.
        if (asset.BoneCount > 0 && (!SkeletonAlive || _skeleton.GetBoneCount() == 0))
        {
            _meshApplied = false;
            return;
        }
        bool anyIndices = false;
        foreach (var sm in mesh.Submeshes)
        {
            if (sm.IndexCount > 0) { anyIndices = true; break; }
        }
        if (!anyIndices)
        {
            _meshApplied = false;
            return;
        }
        if (_meshBuildInFlight)
            return; // a worker build is already running for this hook
        if (_meshApplied && ReferenceEquals(_appliedAsset, asset) && _appliedMeshVcount == mesh.VertexCount
            && (_appliedHideVersion == Owner.LocalHideVersion || SameHidden(_appliedHidden, Owner.LocallyHiddenTriangles)))
            return; // already built this exact mesh - don't rebuild on a lingering dirty flag

        // Triangles something else draws on this machine stay out of the build; read here, on the world side.
        var hidden = Owner.LocallyHiddenTriangles;
        int hideVersion = Owner.LocalHideVersion;

        // Build the heavy Godot ArrayMesh (geometry + per-blendshape arrays + AddSurfaceFromArrays) on a WORKER
        // thread so a dense blendshape mesh doesn't freeze the render thread. Godot 4's RenderingServer is
        // thread-safe, so mesh-resource creation off the main thread is supported; only the scene-tree assignment
        // and the Skin (which needs the Skeleton3D node) run back on the main thread via CallDeferred. We stay a
        // single process - this is just a worker thread - so it's fine on Quest/Pico. -xlinka
        _meshBuildInFlight = true;
        System.Threading.Tasks.Task.Run(() =>
        {
            ArrayMesh built = null!;
            int sc = 0;
            int[] map = System.Array.Empty<int>();
            try
            {
                built = BuildArrayMeshCore(mesh, hidden, out sc, out map);
            }
            catch (System.Exception ex)
            {
                LumoraLogger.Error($"SkinnedMeshHook: off-thread mesh build failed ({ex.Message}); falling back to main thread.");
                built = null!;
            }
            int shapes = sc;
            var surfaces = map;
            MainThreadWork.Post("skinned mesh", () => FinalizeMeshFromAsset(built, shapes, surfaces, mesh, asset, hidden, hideVersion));
        });
    }

    // Every surface add passes these instead of leaving the blend shapes or LODs out. Left out, the binding makes
    // a fresh empty one per call and hands over only its native handle, so nothing holds the wrapper during the
    // call. In an optimized build a collection in the middle of a big surface add finalized it, its storage was
    // freed while the native side still read it, and the process died (0xC0000005, release builds only, one run
    // in two with a crowd arriving). These live for good and are only ever read, from any thread. -xlinka
    private static readonly global::Godot.Collections.Array<global::Godot.Collections.Array> NoBlendShapes = new();
    private static readonly global::Godot.Collections.Dictionary NoLods = new();

    // PURE mesh-resource build - no scene tree, no Owner/data-model writes - so it is safe on a worker thread.
    // Produces the Godot ArrayMesh from the Phos asset. shapeCount returns the blendshape count actually applied
    // (0 if Godot rejected the morph surface), surfaceSubmeshes the submesh each surface was built from. -xlinka
    private ArrayMesh BuildArrayMeshCore(PhosMesh mesh, bool[]? hidden, out int shapeCount, out int[] surfaceSubmeshes)
    {
        int vcount = mesh.VertexCount;

        var arrayMesh = new ArrayMesh();

        var rp = mesh.RawPositions;
        var positions = new Vector3[vcount];
        for (int i = 0; i < vcount; i++)
            positions[i] = new Vector3(rp[i].x, rp[i].y, rp[i].z);

        var rn = mesh.RawNormals;
        Vector3[]? nrmArr = null;
        if (rn != null && rn.Length >= vcount)
        {
            nrmArr = new Vector3[vcount];
            for (int i = 0; i < vcount; i++)
                nrmArr[i] = new Vector3(rn[i].x, rn[i].y, rn[i].z);
        }

        var ruv = mesh.RawUV0s;
        Vector2[]? uvArr = null;
        if (ruv != null && ruv.Length >= vcount)
        {
            uvArr = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
                uvArr[i] = new Vector2(ruv[i].x, ruv[i].y);
        }

        // UV1 (lightmap/detail second channel) so it survives the skinned path, not just the static one. -xlinka
        var ruv1 = mesh.RawUV1s;
        Vector2[]? uv2Arr = null;
        if (ruv1 != null && ruv1.Length >= vcount)
        {
            uv2Arr = new Vector2[vcount];
            for (int i = 0; i < vcount; i++)
                uv2Arr[i] = new Vector2(ruv1[i].x, ruv1[i].y);
        }

        // Tangents (4 floats/vertex: xyz + handedness w) for normal maps, and vertex colors. -xlinka
        float[]? tanArr = null;
        var rtan = mesh.RawTangents;
        if (mesh.HasTangents && rtan != null && rtan.Length >= vcount)
        {
            tanArr = new float[vcount * 4];
            for (int i = 0; i < vcount; i++)
            {
                var t = rtan[i];
                tanArr[i * 4 + 0] = t.x;
                tanArr[i * 4 + 1] = t.y;
                tanArr[i * 4 + 2] = t.z;
                tanArr[i * 4 + 3] = t.w;
            }
        }

        Color[]? colArr = null;
        var rcol = mesh.RawColors;
        if (mesh.HasColors && rcol != null && rcol.Length >= vcount)
        {
            colArr = new Color[vcount];
            for (int i = 0; i < vcount; i++)
            {
                var c = rcol[i];
                colArr[i] = new Color(c.r, c.g, c.b, c.a);
            }
        }

        int[]? boneArr = null;
        float[]? weightArr = null;
        if (mesh.HasBoneBindings)
        {
            var bind = mesh.RawBoneBindings;
            boneArr = new int[vcount * 4];
            weightArr = new float[vcount * 4];
            for (int i = 0; i < vcount; i++)
            {
                var b = bind[i];
                boneArr[i * 4 + 0] = (int)b.boneIndices.x;
                boneArr[i * 4 + 1] = (int)b.boneIndices.y;
                boneArr[i * 4 + 2] = (int)b.boneIndices.z;
                boneArr[i * 4 + 3] = (int)b.boneIndices.w;
                weightArr[i * 4 + 0] = b.boneWeights.x;
                weightArr[i * 4 + 1] = b.boneWeights.y;
                weightArr[i * 4 + 2] = b.boneWeights.z;
                weightArr[i * 4 + 3] = b.boneWeights.w;
            }
        }

        // Every submesh's indices together, for the tangent pass - tangents are a per-VERTEX property and
        // a vertex can be used by any surface, so deriving them from one submesh leaves the rest with a
        // garbage basis.
        int totalIndices = 0;
        foreach (var sm in mesh.Submeshes)
        {
            if (sm != null && sm.Topology == PhosTopology.Triangles)
                totalIndices += sm.IndexCount;
        }

        var allIndices = new int[totalIndices];
        int writeAt = 0;
        foreach (var sm in mesh.Submeshes)
        {
            if (sm == null || sm.IndexCount <= 0 || sm.Topology != PhosTopology.Triangles)
                continue;
            System.Array.Copy(sm.RawIndices, 0, allIndices, writeAt, sm.IndexCount);
            writeAt += sm.IndexCount;
        }

        // No source tangents but we have normals + UVs: GENERATE them. Without tangents a normal-mapped
        // material renders with a garbage tangent basis - the speckled "white cracks" on detailed areas like a
        // muzzle, and unstable/dark lighting when a blendshape (e.g. a blink) perturbs the mesh. -xlinka
        if (tanArr == null && nrmArr != null && uvArr != null && allIndices.Length >= 3)
            tanArr = ComputeTangents(positions, nrmArr, uvArr, allIndices);

        // Blendshapes (morph targets) from the asset: declare them, then hand per-shape delta arrays to each
        // surface. glTF morphs are deltas -> Relative mode; Godot rejects the surface unless each shape carries
        // the SAME morphable attrs as the base (which has NORMAL), so feed the base normals when absent. -xlinka
        shapeCount = mesh.BlendShapeCount;
        if (shapeCount > 0)
        {
            arrayMesh.ClearBlendShapes();
            arrayMesh.BlendShapeMode = Mesh.BlendShapeMode.Relative;
            for (int s = 0; s < shapeCount; s++)
                arrayMesh.AddBlendShape(mesh.BlendShapes[s].Name);
        }

        // ONE SURFACE PER SUBMESH. Only the first was ever built before, which on a character that
        // splits its body across several material groups means most of it simply is not there - the test
        // avatar draws 960 of its 82,540 triangles and reads as a shapeless blob. Each surface carries only
        // the vertices its own triangles use (SurfaceSubset), and the per-surface materials on the renderer
        // are what the split is for. -xlinka
        var subset = new SurfaceSubset(vcount);
        var built = new List<int>();
        int dropped = 0;

        // Where each submesh's triangles start in the count over every triangle submesh, for the hidden set.
        var firstTriangle = new int[mesh.Submeshes.Count];
        int triangleCount = 0;
        for (int index = 0; index < mesh.Submeshes.Count; index++)
        {
            firstTriangle[index] = triangleCount;
            var sm = mesh.Submeshes[index];
            if (sm != null && sm.Topology == PhosTopology.Triangles)
                triangleCount += sm.IndexCount / 3;
        }

        for (int index = 0; index < mesh.Submeshes.Count; index++)
        {
            var sm = mesh.Submeshes[index];
            // Surfaces are drawn as triangles; a points submesh fed in as triangles drew noise or was refused.
            if (sm == null || sm.IndexCount <= 0 || sm.Topology != PhosTopology.Triangles)
                continue;

            int[]? kept = Visible(sm, firstTriangle[index]);
            if (kept != null && kept.Length == 0)
                continue;

            if (built.Count >= MaxGodotMeshSurfaces)
            {
                dropped++;
                continue;
            }

            if (AddSurface(sm, shapeCount, kept))
            {
                built.Add(index);
                continue;
            }

            if (shapeCount == 0)
            {
                LumoraLogger.Warn($"SkinnedMeshHook: submesh {index} ({sm.IndexCount} indices) was refused by the renderer and will not draw.");
                continue;
            }

            // Godot rejected the morph surface. Drop morphs for the WHOLE mesh and restart -
            // blend shapes are declared per mesh, so a half-morphed mesh is not a valid state.
            LumoraLogger.Warn($"SkinnedMeshHook: Phos blend-shape surface rejected ({shapeCount} shapes) - rendering without morphs so the mesh still shows.");
            arrayMesh.ClearSurfaces();
            arrayMesh.ClearBlendShapes();
            shapeCount = 0;
            built.Clear();
            dropped = 0;
            index = -1;
        }

        if (dropped > 0)
            LumoraLogger.Warn($"SkinnedMeshHook: mesh has {mesh.Submeshes.Count} submeshes but Godot caps a mesh at {MaxGodotMeshSurfaces} surfaces - {dropped} surface(s) past the cap will not render.");

        surfaceSubmeshes = built.ToArray();
        return arrayMesh;

        // The submesh's indices without the hidden triangles, or null when none of them is hidden.
        int[]? Visible(PhosSubmesh sm, int first)
        {
            if (hidden == null)
                return null;
            int triangles = sm.IndexCount / 3;
            int keep = 0;
            for (int t = 0; t < triangles; t++)
                if (first + t >= hidden.Length || !hidden[first + t])
                    keep++;
            if (keep == triangles)
                return null;
            var result = new int[keep * 3];
            int at = 0;
            var raw = sm.RawIndices;
            for (int t = 0; t < triangles; t++)
            {
                if (first + t < hidden.Length && hidden[first + t])
                    continue;
                result[at++] = raw[t * 3];
                result[at++] = raw[t * 3 + 1];
                result[at++] = raw[t * 3 + 2];
            }
            return result;
        }

        bool AddSurface(PhosSubmesh sm, int shapes, int[]? kept)
        {
            var source = kept ?? sm.RawIndices;
            int sourceCount = kept?.Length ?? sm.IndexCount;
            bool part = subset.Take(source, sourceCount);
            System.Span<int> used = part ? subset.Vertices : System.Span<int>.Empty;
            int count = part ? used.Length : vcount;

            // Every Godot.Collections.Array here owns NATIVE storage, and AddSurfaceFromArrays copies out of
            // it rather than taking ownership. Left undisposed the native buffers only come back at
            // finalization, so an avatar with hundreds of morph targets sat on a second full copy of its
            // geometry for as long as the GC felt like it. Released the moment the surface is on. -xlinka
            var arrays = new global::Godot.Collections.Array();
            var shapeArrays = new List<global::Godot.Collections.Array>();
            global::Godot.Collections.Array<global::Godot.Collections.Array>? blendShapes = null;
            try
            {
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = part ? SurfaceSubset.Gather(positions, used) : positions;
                if (nrmArr != null)
                    arrays[(int)Mesh.ArrayType.Normal] = part ? SurfaceSubset.Gather(nrmArr, used) : nrmArr;
                if (uvArr != null)
                    arrays[(int)Mesh.ArrayType.TexUV] = part ? SurfaceSubset.Gather(uvArr, used) : uvArr;
                if (uv2Arr != null)
                    arrays[(int)Mesh.ArrayType.TexUV2] = part ? SurfaceSubset.Gather(uv2Arr, used) : uv2Arr;
                float[]? extraUV = null;
                var uvFormat = MeshUVChannels.Set(arrays, mesh, ref extraUV, part ? used : default);
                if (tanArr != null)
                    arrays[(int)Mesh.ArrayType.Tangent] = part ? SurfaceSubset.Gather4(tanArr, used) : tanArr;
                if (colArr != null)
                    arrays[(int)Mesh.ArrayType.Color] = part ? SurfaceSubset.Gather(colArr, used) : colArr;
                if (boneArr != null && weightArr != null)
                {
                    arrays[(int)Mesh.ArrayType.Bones] = part ? SurfaceSubset.Gather4(boneArr, used) : boneArr;
                    arrays[(int)Mesh.ArrayType.Weights] = part ? SurfaceSubset.Gather4(weightArr, used) : weightArr;
                }

                if (part)
                {
                    arrays[(int)Mesh.ArrayType.Index] = Variant.CreateFrom(subset.Indices);
                }
                else
                {
                    var indices = new int[sourceCount];
                    System.Array.Copy(source, indices, sourceCount);
                    arrays[(int)Mesh.ArrayType.Index] = indices;
                }

                int before = arrayMesh.GetSurfaceCount();
                if (shapes <= 0)
                {
                    arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, NoBlendShapes, NoLods, uvFormat);
                    return arrayMesh.GetSurfaceCount() > before;
                }

                blendShapes = new global::Godot.Collections.Array<global::Godot.Collections.Array>();

                // ONE staging buffer for every shape. Assigning it into a shape's array marshals it into a
                // packed Godot array, which is a copy, so the managed buffer is dead the moment the assignment
                // returns and a fresh Vector3[] per shape was 756 * 58,030 * 12 bytes of pure garbage on the
                // avatar that started this. Every element is overwritten below, so no clear is needed. -xlinka
                var sv = new Vector3[count];

                for (int s = 0; s < shapes; s++)
                {
                    var frame = mesh.BlendShapes[s].Frames.Length > 0 ? mesh.BlendShapes[s].Frames[0] : null;
                    var shapeArray = new global::Godot.Collections.Array();
                    shapeArray.Resize((int)Mesh.ArrayType.Max);
                    shapeArrays.Add(shapeArray);

                    var sp = frame?.positions;
                    for (int k = 0; k < count; k++)
                    {
                        int i = part ? used[k] : k;
                        sv[k] = (sp != null && i < sp.Length) ? new Vector3(sp[i].x, sp[i].y, sp[i].z) : Vector3.Zero;
                    }
                    shapeArray[(int)Mesh.ArrayType.Vertex] = sv;

                    // "No normal/tangent morph" = feed the BASE normals/tangents. NOT zeros. NEVER FUCKING ZEROS.
                    // This innocent-looking array cost DAYS: Godot packs mesh normals/tangents octahedral-encoded,
                    // and octahedral can only represent UNIT vectors - a zero vector encodes as 0/0 = NaN garbage,
                    // and the blend path then happily mixes that garbage in PROPORTIONALLY TO THE WEIGHT. Result:
                    // the whole goddamn avatar's shading went dark every single blink, no rebuild in the log,
                    // nothing wrong in the data model, just Godot quietly shitting NaNs into the normal buffer
                    // where no debugger looks. Third attempt at this line: Assimp's real morph normals were noise
                    // (whole mesh dark always), zeros "fixed" it into a weight-proportional skew (dark only WHILE
                    // blinking, the absolute worst kind of subtle), base arrays are the correct end state - safe
                    // under every blend convention: as a target the base IS "unchanged", as an added delta
                    // base + w*base renormalizes to the same direction in the fragment shader. If you are reading
                    // this because an avatar's lighting pulses with its face: it's this. It's always this. - xlinka
                    // The base here is this SURFACE's base: a shape has to line up with the surface it rides on.
                    shapeArray[(int)Mesh.ArrayType.Normal] = arrays[(int)Mesh.ArrayType.Normal];
                    if (tanArr != null)
                        shapeArray[(int)Mesh.ArrayType.Tangent] = arrays[(int)Mesh.ArrayType.Tangent];

                    blendShapes.Add(shapeArray);
                }

                arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, blendShapes, NoLods, uvFormat);
                return arrayMesh.GetSurfaceCount() > before;
            }
            finally
            {
                for (int i = 0; i < shapeArrays.Count; i++)
                    shapeArrays[i].Dispose();
                // Array<T> is a typed view over an untyped Array; the untyped one is what owns the storage.
                if (blendShapes != null)
                    ((global::Godot.Collections.Array)blendShapes).Dispose();
                arrays.Dispose();
            }
        }
    }

    // Main-thread finalize: assign the built mesh to the scene node, build the Skin (needs the Skeleton3D), mirror
    // blendshape names, apply material. Falls back to a synchronous on-main build if the worker build threw. -xlinka
    private void FinalizeMeshFromAsset(ArrayMesh built, int shapeCount, int[] surfaceSubmeshes, PhosMesh mesh, MeshDataAsset asset,
        bool[]? hidden, int hideVersion)
    {
        _meshBuildInFlight = false;

        if (!MeshInstanceAlive || Owner.IsDestroyed)
        {
            built?.Dispose();
            return; // hook/instance was torn down while the worker built
        }
        if (!ReferenceEquals(Owner.MeshAsset.Asset, asset) || !ReferenceEquals(asset.MeshData, mesh))
        {
            // The renderer changed assets while this worker ran. Never let a late completion put
            // the previous mesh back onto it; schedule the current request without a user edit.
            built?.Dispose();
            Owner.World.RunSynchronously(() =>
            {
                if (!Owner.IsDestroyed)
                {
                    Owner.MeshDataChanged = true;
                    Owner.HookBindingComplete = false;
                    Owner.MarkChangeDirty();
                }
            });
            return;
        }

        // Diagnostic: did the WORKER build succeed, or are we rebuilding on the main thread (= the freeze)? -xlinka
        bool offThreadOk = built != null;
        long _finalizeStart = System.Diagnostics.Stopwatch.GetTimestamp();

        if (built == null)
        {
            try
            {
                built = BuildArrayMeshCore(mesh, hidden, out shapeCount, out surfaceSubmeshes);
            }
            catch (System.Exception ex)
            {
                LumoraLogger.Error($"SkinnedMeshHook: main-thread fallback mesh build failed: {ex.Message}");
                return;
            }
        }

        var old = _arrayMesh;
        _arrayMesh = built;
        _surfaceSubmeshes = surfaceSubmeshes;
        _meshInstance.Mesh = _arrayMesh;
        old?.Dispose();

        // Godot's cull box for a skinned instance follows the skeleton (a copy's hips moved 3 m and the renderer
        // found all its instances at the new spot, none at the old), so the margin only covers what bones do not
        // carry: morphs. The old 4 m floor dragged every avatar into each probe face, shadow cascade and the view
        // behind the camera within reach of it. -xlinka
        var restAabb = _arrayMesh.GetAabb();
        _meshInstance.ExtraCullMargin = restAabb.Size.Length();

        // A skinned avatar is a DYNAMIC mesh, but Godot defaults a MeshInstance3D to GI_MODE_STATIC, which bakes the
        // rest pose into global illumination. Once the avatar is equipped and posed/moving, that static GI no longer
        // matches the geometry and the mesh lights incorrectly (typically darker). Disable GI contribution - the
        // avatar still RECEIVES light, it just stops polluting/relying on static GI. -xlinka
        _meshInstance.GIMode = GeometryInstance3D.GIModeEnum.Disabled;

        // Mirror the asset's blendshape names onto the component so expression/viseme/blink drivers can find them.
        // These are SyncFieldList (data-model) writes that MUST run under the world lock. This finalize runs via a
        // Godot CallDeferred (off-lock), so writing them HERE trips HookManager.ThreadCheck and throws - which
        // aborted the finalize before the Skin + material were applied, leaving the mesh unskinned (rest-pose, so it
        // renders huge/distorted) and untextured (grey fallback). Queue the mirror onto the world's synchronous
        // action pass (runs under the Implementer lock), with a system bypass so it still applies once the avatar is
        // equipped/host-owned. -xlinka
        if (shapeCount > 0 && Owner.BlendShapeNames.Count != shapeCount)
        {
            var names = new string[shapeCount];
            for (int s = 0; s < shapeCount; s++)
                names[s] = mesh.BlendShapes[s].Name;
            var owner = Owner;
            owner.World?.RunSynchronously(() =>
            {
                if (owner == null || owner.IsDestroyed)
                    return;
                using (owner.World?.DataModelPermissions?.EnterSystemBypass())
                {
                    owner.BlendShapeNames.Clear();
                    for (int s = 0; s < names.Length; s++)
                        owner.BlendShapeNames.Add(names[s]);
                    // Sized to the shape count, never cleared: the weights are data that arrive with
                    // the component (a package carries the creator's body sliders in them, a save its
                    // own), and they land before the mesh does. Clearing here threw every one of them
                    // away and re-added zeros. -xlinka
                    while (owner.BlendShapeWeights.Count > names.Length)
                        owner.BlendShapeWeights.RemoveAt(owner.BlendShapeWeights.Count - 1);
                    while (owner.BlendShapeWeights.Count < names.Length)
                        owner.BlendShapeWeights.Add(0f);
                }
                ApplyBlendShapeWeights();
            });
        }

        BuildAndAssignSkinFromAsset(asset);
        ApplyBlendShapeWeights();

        // Material: real asset if loaded, else a non-latching fallback the update loop will replace once the PBS
        // asset finishes loading.
        ApplyMaterial();

        _appliedAsset = asset;
        _appliedMeshVcount = mesh.VertexCount;
        _appliedHideVersion = hideVersion;
        _appliedHidden = hidden;
        _meshApplied = true;
        Owner.HookBindingComplete = true;
        RefreshEyeViewTwin();

        // The triangles to leave out changed while this one was building: build again with the new set.
        if (hideVersion != Owner.LocalHideVersion && !SameHidden(hidden, Owner.LocallyHiddenTriangles))
        {
            var owner = Owner;
            owner.World?.RunSynchronously(() =>
            {
                if (!owner.IsDestroyed)
                {
                    owner.MeshDataChanged = true;
                    owner.MarkChangeDirty();
                }
            });
        }

        // HookBindingComplete above switches off the component's per-update re-drive, so from here on
        // nothing asks this hook to run again unless something else touches the renderer. If the
        // ApplyMaterial just above left a surface on the loading skin, this is the only thing that
        // brings the real material in when its textures land. -xlinka
        RequeueWhilePlaceholderShown();
        if (!LumoraLogger.EnableDebug)
            return;
        double _finalizeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _finalizeStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // One line per skinned mesh, at Debug. A mesh can build, skin and pass every check and still not
        // appear - wrong visibility, an empty surface, or bounds the renderer culls against - and none
        // of that is visible from the outside. This is the line that says which. -xlinka
        var aabb = _meshInstance.GetAabb();
        int surfaces = _arrayMesh?.GetSurfaceCount() ?? 0;
        int skeletonBones = (_skeleton != null && GodotObject.IsInstanceValid(_skeleton)) ? _skeleton.GetBoneCount() : -1;
        LumoraLogger.Debug($"SkinnedMeshHook '{Owner.Slot?.SlotName.Value}': {mesh.VertexCount} verts, {surfaces} surface(s), "
                       + $"{mesh.BlendShapeCount} shapes, {asset.BoneCount} bind bones vs {skeletonBones} skeleton bones | "
                       + $"visible={_meshInstance.Visible} enabled={Owner.Enabled} materials={Owner.Materials.Count} "
                       + $"aabb=({aabb.Size.X:F2},{aabb.Size.Y:F2},{aabb.Size.Z:F2}) at ({aabb.Position.X:F2},{aabb.Position.Y:F2},{aabb.Position.Z:F2}) "
                       // WHERE the Godot node actually is, and on which layers. A mesh reporting
                       // visible=True still photographs as nothing if its instance sits somewhere else
                       // or renders on a layer the viewing camera does not include. -xlinka
                       + $"node=({_meshInstance!.GlobalPosition.X:F2},{_meshInstance.GlobalPosition.Y:F2},{_meshInstance.GlobalPosition.Z:F2}) "
                       + $"scale=({_meshInstance.Scale.X:F2},{_meshInstance.Scale.Y:F2},{_meshInstance.Scale.Z:F2}) "
                       + $"layers={_meshInstance.Layers} | "
                       // THE GLOBAL CHAIN, not the local transform.
                       //
                       // scale= above is the instance's own local, which is 1 on every skinned mesh
                       // because the instance is reparented under the Skeleton3D. What decides where
                       // the skin actually lands is the SKELETON'S global, and a sub-rig imported from
                       // an FBX carries a unit conversion there - the radio's is 0.012 x 0.0833. A
                       // 400-unit bind-space mesh under a 0.001 skeleton is a 40 cm antenna; under a
                       // 1.0 skeleton it is a disaster, and these two numbers are the difference.
                       // -xlinka
                       + $"skelGlobalScale=({SkeletonGlobalScale().X:F4},{SkeletonGlobalScale().Y:F4},{SkeletonGlobalScale().Z:F4}) "
                       + $"skelGlobalPos=({SkeletonGlobalOrigin().X:F2},{SkeletonGlobalOrigin().Y:F2},{SkeletonGlobalOrigin().Z:F2}) | "
                       + $"offThreadBuild={offThreadOk}, finalize={_finalizeMs:F0}ms");
    }

    private Vector3 SkeletonGlobalScale()
        => (_skeleton != null && GodotObject.IsInstanceValid(_skeleton))
            ? _skeleton.GlobalTransform.Basis.Scale
            : Vector3.Zero;

    private Vector3 SkeletonGlobalOrigin()
        => (_skeleton != null && GodotObject.IsInstanceValid(_skeleton))
            ? _skeleton.GlobalTransform.Origin
            : Vector3.Zero;

    // The joint resting nearest to a point, and of the joints nearly as near the one closest to the root: a neck
    // rides the chest beside it rather than a strand of hair that happens to hang there.
    private int NearestRestBone(Vector3 at)
    {
        int count = _skeleton.GetBoneCount();
        float nearest = float.MaxValue;
        for (int b = 0; b < count; b++)
            nearest = System.Math.Min(nearest, _skeleton.GetBoneGlobalRest(b).Origin.DistanceTo(at));
        float reach = nearest * 2f + 1e-4f;
        int best = 0, bestDepth = int.MaxValue;
        float bestDistance = float.MaxValue;
        for (int b = 0; b < count; b++)
        {
            float distance = _skeleton.GetBoneGlobalRest(b).Origin.DistanceTo(at);
            if (distance > reach)
                continue;
            int depth = 0;
            for (int p = _skeleton.GetBoneParent(b); p >= 0 && depth < 256; p = _skeleton.GetBoneParent(p))
                depth++;
            if (depth < bestDepth || (depth == bestDepth && distance < bestDistance))
            {
                best = b;
                bestDepth = depth;
                bestDistance = distance;
            }
        }
        return best;
    }

    // Map every mesh bone (in the asset's bone table) to its skeleton bone BY NAME and stamp the asset's bind
    // pose, so the surface's mesh-space bone indices resolve to the right skeleton bone on the GPU. -xlinka
    private void BuildAndAssignSkinFromAsset(MeshDataAsset asset)
    {
        // Runs deferred, after an off-thread mesh build - the skeleton can have been rebuilt (and the
        // old node freed) in the meantime, so revalidate rather than trusting the cached pointer.
        if (!SkeletonAlive || !MeshInstanceAlive || asset.BoneCount == 0)
            return;

        // Names that differ only by decoration, a duplicate suffix or a typo still mean the same joint,
        // so fall back to a fuzzy match rather than collapsing the bone to the root. A real avatar binds
        // "Left_FirstToe" against a slot called "Left_FirstToey" - one stray character, and without this
        // the toe verts get yanked to the origin. Ambiguous matches are refused, not guessed.
        var matcher = new Lumora.Core.Assets.BoneNameMatcher();
        for (int b = 0; b < _skeleton.GetBoneCount(); b++)
            matcher.Add(BindNameOf(b), b);

        var skin = new Skin();
        skin.SetBindCount(asset.BoneCount);
        int unresolved = 0;
        string firstUnresolved = string.Empty;
        int fuzzy = 0;
        for (int i = 0; i < asset.BoneCount; i++)
        {
            string boneName = asset.GetBoneName(i) ?? string.Empty;
            var bind = asset.GetBoneBindPose(i).ToGodot();
            if (!matcher.TryResolve(boneName, out int skelBone, out var kind))
            {
                // Nothing plausible in the skeleton (an FBX pivot, a bone the rig never had). Its own bind pose on
                // some other joint throws its verts across the room: a streak from the body to wherever that joint's
                // space puts them. So they ride a joint resting beside where this bone rested (NearestRestBone),
                // held where they are at rest. -xlinka
                unresolved++;
                skelBone = NearestRestBone(bind.AffineInverse().Origin);
                if (unresolved == 1)
                    firstUnresolved = $"'{boneName}' rides '{_skeleton.GetBoneName(skelBone)}'";
                if (unresolved <= 8)
                    LumoraLogger.Debug($"SkinnedMeshHook: bind bone '{boneName}' not in skeleton - its verts ride '{_skeleton.GetBoneName(skelBone)}', resting beside it.");
                bind = _skeleton.GetBoneGlobalRest(skelBone).AffineInverse();
            }
            else if (kind != Lumora.Core.Assets.BoneNameMatcher.MatchKind.Exact)
            {
                fuzzy++;
                if (fuzzy <= 8)
                    LumoraLogger.Debug($"SkinnedMeshHook: bind bone '{boneName}' matched '{_skeleton.GetBoneName(skelBone)}' by {kind} - names differ, joint is the same.");
            }
            skin.SetBindBone(i, skelBone);
            skin.SetBindPose(i, bind);
        }
        if (fuzzy > 8)
            LumoraLogger.Debug($"SkinnedMeshHook: {fuzzy} bind bones matched by name normalization.");
        if (unresolved > 0)
            LumoraLogger.Warn($"SkinnedMeshHook: {unresolved}/{asset.BoneCount} bind bones unresolved against skeleton '{_skeleton.Name}' ({_skeleton.GetBoneCount()} bones); first {firstUnresolved}; each rides the joint nearest where it rested.");

        // Skinning canary (one-time per build): at rest, GlobalBoneRest * BindPose must be ~identity (origin ~0,
        // basis det ~1) or that bone's verts deform. Scan ALL bones and flag the bad ones BY NAME - this catches a
        // SUBSET deforming (e.g. ears) while the body is fine, which a first-3-bones spot-check would miss. -xlinka
        int badBones = 0;
        for (int i = 0; i < asset.BoneCount; i++)
        {
            int skelBone = FindSkeletonBone(asset.GetBoneName(i));
            if (skelBone < 0) continue;
            var check = _skeleton.GetBoneGlobalRest(skelBone) * asset.GetBoneBindPose(i).ToGodot();
            float originErr = check.Origin.Length();
            float detErr = System.Math.Abs(check.Basis.Determinant() - 1f);
            if (originErr > 0.02f || detErr > 0.05f)
            {
                badBones++;
                if (badBones <= 16)
                    LumoraLogger.Debug($"SkinnedMeshHook[skin-check]: BONE '{asset.GetBoneName(i)}' rest*bind NOT identity - originErr={originErr:F3} detErr={detErr:F3} (its verts deform).");
            }
        }
        // Once per mesh a run: the same skin built again for every viewer, copy and rebuild said the same thing
        // over again, bone by bone, a thousand lines an evening.
        if (badBones > 0 && SkinChecked(asset))
            LumoraLogger.Warn($"SkinnedMeshHook[skin-check]: {badBones}/{asset.BoneCount} bones FAIL rest*bind==identity on mesh '{Owner.Slot?.SlotName.Value}'.");
        else
            LumoraLogger.Debug($"SkinnedMeshHook[skin-check]: all {asset.BoneCount} bones OK at rest on mesh '{Owner.Slot?.SlotName.Value}'.");

        _meshInstance.Skin = skin;
    }

    private static readonly HashSet<string> _skinChecked = new();

    private static bool SkinChecked(MeshDataAsset asset)
    {
        string key = asset.AssetURL?.OriginalString ?? asset.GetHashCode().ToString();
        lock (_skinChecked)
            return _skinChecked.Add(key);
    }

    // EYE-VIEW TWIN

    public void AddEyeViewCollapse(LocalViewOverride view)
    {
        if (view == null || _eyeCollapses.Contains(view))
            return;
        _eyeCollapses.Add(view);
        RefreshEyeViewTwin();
    }

    public void RemoveEyeViewCollapse(LocalViewOverride view)
    {
        if (view != null && _eyeCollapses.Remove(view))
            RefreshEyeViewTwin();
    }

    // Reconcile the twin with what is wanted and what is bound. Called on every registration edge and
    // after every bind or mesh finalize, so an override that registered before the mesh existed gets
    // its twin the moment the mesh does.
    private void RefreshEyeViewTwin()
    {
        _eyeCollapses.RemoveAll(v => v == null || v.IsDestroyed || v.Slot == null || v.Slot.IsDestroyed);

        bool wanted = _eyeCollapses.Count > 0
            && MeshInstanceAlive && SkeletonAlive && _skeletonBound && _meshApplied
            && _arrayMesh != null && _skeletonHook != null;

        if (!wanted)
        {
            DestroyEyeViewTwin(logIt: true);
            return;
        }

        if (_twin != null && !_twin.Alive)
            DestroyEyeViewTwin(logIt: false);

        var collapsed = new List<int>(_eyeCollapses.Count);
        foreach (var view in _eyeCollapses)
        {
            int index = _skeletonHook!.GetBoneIndex(view.Slot);
            if (index >= 0)
                collapsed.Add(index);
        }
        // A bone this skeleton does not carry (a stale registration, or the override sits on a slot the
        // importer never made a bone) is nothing to collapse, and a twin with nothing to collapse is a
        // second draw of the whole body for no reason.
        if (collapsed.Count == 0 || !MeshTouchesCollapsed(collapsed))
        {
            DestroyEyeViewTwin(logIt: true);
            return;
        }

        if (_twin == null && !CreateEyeViewTwin())
            return;

        _twin!.SetCollapsedBones(collapsed);
        // Collapse now rather than on the skeleton's next pass: the twin is what the eyes draw from this
        // frame on, and one frame of the inside of the head is exactly the flash this exists to remove.
        _twin.FinishPoses();
        MirrorTwinAppearance();
    }

    // Whether any vertex of this mesh hangs off a collapsed bone or a bone under one. Only such a mesh has
    // anything for the eyes not to see; clothes, a tail or a body without the head on it got a twin all the
    // same, so every mesh of the worn avatar was skinned twice a frame and posed on a skeleton of its own.
    // A mesh it cannot read counts as touching, which keeps the twin. -xlinka
    private bool MeshTouchesCollapsed(List<int> collapsed)
    {
        var skin = MeshInstanceAlive ? _meshInstance.Skin : null;
        var mesh = _appliedAsset?.MeshData;
        if (skin == null || mesh == null || !mesh.HasBoneBindings || !SkeletonAlive)
            return true;

        int bones = _skeleton.GetBoneCount();
        var hidden = new bool[bones];
        foreach (int index in collapsed)
        {
            if (index >= 0 && index < bones)
                hidden[index] = true;
        }
        // Parents come before their children in the bone table, so one pass carries a collapse down the tree.
        for (int b = 0; b < bones; b++)
        {
            int parent = _skeleton.GetBoneParent(b);
            if (parent >= 0 && hidden[parent])
                hidden[b] = true;
        }

        int binds = skin.GetBindCount();
        var bindHidden = new bool[binds];
        bool any = false;
        for (int i = 0; i < binds; i++)
        {
            int bone = skin.GetBindBone(i);
            if (bone >= 0 && bone < bones && hidden[bone])
                any = bindHidden[i] = true;
        }
        if (!any)
            return false;

        var bindings = mesh.RawBoneBindings;
        int count = System.Math.Min(mesh.VertexCount, bindings?.Length ?? 0);
        for (int v = 0; v < count; v++)
        {
            var b = bindings![v];
            if (Hits(b.boneIndices.x, b.boneWeights.x) || Hits(b.boneIndices.y, b.boneWeights.y)
                || Hits(b.boneIndices.z, b.boneWeights.z) || Hits(b.boneIndices.w, b.boneWeights.w))
                return true;
        }
        return false;

        bool Hits(float index, float weight)
        {
            int i = (int)index;
            return weight > 0f && i >= 0 && i < binds && bindHidden[i];
        }
    }

    private bool CreateEyeViewTwin()
    {
        var twinSkeleton = _skeletonHook!.CreateTwinSkeleton();
        if (twinSkeleton == null)
            return false;

        var instance = new MeshInstance3D
        {
            Name = "EyeViewInstance",
            Mesh = _arrayMesh,
            Skin = _meshInstance.Skin,
            Layers = (uint)RenderHelper.EYES_ONLY_LAYER,
            // The real instance keeps casting: the shadow on the floor should still have a head.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            ExtraCullMargin = _meshInstance.ExtraCullMargin,
            Transform = Transform3D.Identity,
        };
        twinSkeleton.AddChild(instance);
        instance.Skeleton = new NodePath("..");

        _twin = new EyeViewTwin(twinSkeleton, instance);
        _twinMesh = _arrayMesh;
        _skeletonHook.RegisterTwin(_twin);

        // The eyes draw the twin instead of the body: park the real instance where only they cannot see.
        if (!_realLayersSwapped)
        {
            _realLayersBeforeTwin = _meshInstance.Layers;
            _realLayersSwapped = true;
        }
        _meshInstance.Layers = (uint)RenderHelper.LOCAL_HEAD_LAYER;

        var bones = new System.Text.StringBuilder();
        foreach (var view in _eyeCollapses)
        {
            if (bones.Length > 0) bones.Append(", ");
            bones.Append(view.Slot?.SlotName.Value);
        }
        LumoraLogger.Debug($"LocalViewOverride: eye-view twin for '{Owner.Slot?.SlotName.Value}' collapsing '{bones}'");
        return true;
    }

    private void DestroyEyeViewTwin(bool logIt)
    {
        if (_twin != null)
        {
            _skeletonHook?.UnregisterTwin(_twin);
            _twin.Free();
            if (logIt)
                LumoraLogger.Debug($"LocalViewOverride: eye-view twin for '{Owner?.Slot?.SlotName.Value}' released");
            _twin = null;
            _twinMesh = null;
        }

        if (_realLayersSwapped)
        {
            if (MeshInstanceAlive)
                _meshInstance.Layers = _realLayersBeforeTwin;
            _realLayersSwapped = false;
        }
    }

    // Everything the twin shares with the real instance that is not a bone pose: mesh, skin, cull
    // margin, materials, morph weights, LOD band, visibility. Cheap, and only runs on twin edges and
    // mesh rebuilds; the per-frame pieces (weights, materials) write both instances where they happen.
    private void MirrorTwinAppearance()
    {
        if (_twin == null || !_twin.Alive || !MeshInstanceAlive || _arrayMesh == null)
            return;

        var twin = _twin.Instance;
        if (!ReferenceEquals(_twinMesh, _arrayMesh))
        {
            twin.Mesh = _arrayMesh;
            _twinMesh = _arrayMesh;
        }
        var skin = _meshInstance.Skin;
        if (twin.Skin != skin)
            twin.Skin = skin;
        twin.ExtraCullMargin = _meshInstance.ExtraCullMargin;
        twin.Visible = _meshInstance.Visible;
        VisibleRange.ApplyTo(twin);

        int surfaces = _arrayMesh.GetSurfaceCount();
        for (int surface = 0; surface < surfaces; surface++)
            twin.SetSurfaceOverrideMaterial(surface, _meshInstance.GetSurfaceOverrideMaterial(surface));

        ApplyBlendShapeWeights();
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld)
            DestroyEyeViewTwin(logIt: false);
        _eyeCollapses.Clear();
        _twin = null;
        _twinMesh = null;

        if (!destroyingWorld && _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.QueueFree();
        }

        _arrayMesh?.Dispose();

        _meshInstance = null!;
        _arrayMesh = null!;
        _skeletonHook = null!;
        _skeleton = null!;
        _boneIndexMap.Clear();

        base.Destroy(destroyingWorld);
    }
}
