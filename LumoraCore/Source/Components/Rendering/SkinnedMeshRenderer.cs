// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Core.Networking.Sync;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Components;

[ComponentCategory("Rendering")]
public class SkinnedMeshRenderer : ImplementableComponent, ICustomMemberNameSource
{
    // BONE REFERENCES

    // bone index 0 in mesh data corresponds to Bones[0], etc.
    // Readonly fields, never properties: member discovery only sees init-only ISyncMember fields, and a
    // list it cannot see is never initialized before OnAwake, replicated, or saved. -xlinka
    public readonly SyncRefList<Slot> Bones = new();

    public readonly SyncFieldList<string> BoneNames = new();

    public readonly SyncRef<SkeletonBuilder> Skeleton = null!;

    // Phos mesh asset source (the universal pipeline). When set, the hook sources geometry, bone bindings AND
    // bind poses from this content-hashed asset and drives skinning through an explicit Skin - instead of the
    // inline Vertices/BoneIndices/BoneWeights lists below (which exist for the legacy Godot-glTF import path).
    // So a skinned avatar travels as one asset instead of thousands of synced elements.
    // MUST be an AssetRef, not a plain SyncRef: an AssetRef reference-counts the MeshProvider (so its underlying
    // MeshDataAsset is actually requested + decoded - a StaticAssetProvider only loads while AssetReferenceCount
    // > 0) AND re-fires ApplyChanges when the async decode finishes (AssetRef.AssetUpdated). A bare SyncRef did
    // NEITHER, so the provider never loaded and a Phos-imported avatar rendered as nothing. -xlinka
    public readonly AssetRef<MeshDataAsset> MeshAsset = null!;

    // MESH DATA

    public readonly SyncFieldList<float3> Vertices = new();

    public readonly SyncFieldList<float3> Normals = new();

    public readonly SyncFieldList<float2> UVs = new();

    public readonly SyncFieldList<int> Indices = new();

    // up to 4 bones per vertex; indices reference the Bones list
    public readonly SyncFieldList<int4> BoneIndices = new();

    public readonly SyncFieldList<float4> BoneWeights = new();

    // BLENDSHAPES (morph targets, e.g. facial expressions / visemes / blink)

    // mesh-level, shared across surfaces of a mesh
    public readonly SyncFieldList<string> BlendShapeNames = new();

    // flattened: shape k occupies [k*VertexCount, (k+1)*VertexCount); stored exactly as the source mesh
    // reported them so the round-trip honors BlendShapeMode
    public readonly SyncFieldList<float3> BlendShapeVertices = new();

    // Per-shape vertex NORMAL deltas, flattened like BlendShapeVertices. Optional - empty when the
    // source carried no normal morph data; when present the hook morphs normals too, so lighting follows the
    // expression (matches how the morph was authored, with per-frame normal deltas). -xlinka
    public readonly SyncFieldList<float3> BlendShapeNormals = new();

    // true when normal morph deltas are present for every shape vertex, so the hook morphs normals too
    public bool HasBlendShapeNormals => BlendShapeVertices.Count > 0 && BlendShapeNormals.Count == BlendShapeVertices.Count;

    // 0..1; driven by expression/viseme/blink drivers. The range puts a slider on every entry in the
    // inspector, and the rows carry the shape names (see GetMemberName), one weight per mesh shape.
    [Range(0f, 1f, "F2")]
    public readonly SyncFieldList<float> BlendShapeWeights = new();

    // 0 = Normalized, 1 = Relative (Godot Mesh.BlendShapeMode)
    public readonly Sync<int> BlendShapeMode = new();

    // set when only weights changed, so the hook reapplies weights without a full mesh rebuild
    public bool BlendWeightsChanged { get; set; }

    public int BlendShapeCount => BlendShapeNames.Count;

    public string BlendShapeName(int index)
        => (index >= 0 && index < BlendShapeNames.Count) ? BlendShapeNames[index] : null!;

    public int GetBlendShapeIndex(string name)
    {
        for (int i = 0; i < BlendShapeNames.Count; i++)
        {
            if (string.Equals(BlendShapeNames[i], name, System.StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public float GetBlendShapeWeight(int index)
        => (index >= 0 && index < BlendShapeWeights.Count) ? BlendShapeWeights[index] : 0f;

    public void SetBlendShapeWeight(int index, float weight)
    {
        if (index < 0 || index >= BlendShapeWeights.Count)
            return;
        if (BlendShapeWeights[index] == weight)
            return;
        BlendShapeWeights[index] = weight;
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    public void SetBlendShapeWeight(string name, float weight)
        => SetBlendShapeWeight(GetBlendShapeIndex(name), weight);

    // The weights list is one entry per mesh shape, so its rows are the shape names and nobody gets to
    // add, remove or reorder them from the inspector: the index IS the shape.
    public string? GetMemberName(ISyncMember element)
    {
        if (element is not Sync<float> weight || !ReferenceEquals(weight.Parent, BlendShapeWeights))
            return null;
        int index = BlendShapeWeights.IndexOfElement(weight);
        return index >= 0 ? BlendShapeName(index) : null;
    }

    public bool OwnsCollectionShape(ISyncMember collection) => ReferenceEquals(collection, BlendShapeWeights);

    // A weight edited through its own field (inspector slider, a peer's edit, a load) never passed
    // through SetBlendShapeWeight, so the hook was never told and the mesh sat on the old value until
    // something unrelated rebuilt it. The list only reports adds and removes; each element's own change
    // is hooked here as it arrives. -xlinka
    private void HookBlendShapeWeightElements(int index, int count)
    {
        for (int i = index; i < index + count && i < BlendShapeWeights.Count; i++)
            BlendShapeWeights.GetElement(i).OnChanged += OnBlendShapeWeightChanged;
    }

    private void UnhookBlendShapeWeightElements(int index, int count)
    {
        for (int i = index; i < index + count && i < BlendShapeWeights.Count; i++)
            BlendShapeWeights.GetElement(i).OnChanged -= OnBlendShapeWeightChanged;
    }

    private void OnBlendShapeWeightChanged(float value)
    {
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    // Local (non-replicated) per-frame override buffer, written by blink/viseme/expression drivers.
    // Every peer runs those drivers from replicated inputs (voice, timers), so broadcasting per-frame
    // weights would just be churn - same reasoning as the IK bone writes.
    private float[]? _runtimeWeights;

    // Blendshape ownership: one owner per shape, like a per-element field drive. A face driver
    // claims the shapes it animates; a higher priority wins regardless of resolve order, so blink/viseme own
    // their shapes over the catch-all expression driver. Drivers re-check ownership at drive time, so a shape
    // that gets stolen stops being written by the loser - this is what stops drivers from fighting (flicker).
    // Local/runtime only (the drivers run on every peer from replicated inputs), not synced.
    // A hand gesture's face sits over speech but under the eyes and lids; an avatar's own switches (a
    // hidden shirt, a tail size) sit over the automatic face drivers, and the pose an avatar rests in sits
    // under its switches. -xlinka
    public const int BlendShapePriorityBlink = 30;
    public const int BlendShapePriorityEye = 25;
    public const int BlendShapePriorityGesture = 22;
    public const int BlendShapePriorityViseme = 20;
    public const int BlendShapePriorityToggle = 15;
    public const int BlendShapePriorityRest = 12;
    // Face-tracking shapes the avatar names itself win over the same driver's guesses by name.
    public const int BlendShapePriorityNamedExpression = 11;
    public const int BlendShapePriorityExpression = 10;

    private object?[]? _blendShapeOwners;
    private int[]? _blendShapeOwnerPriority;

    // succeeds if unclaimed, already yours, or your priority beats the current owner's (a steal); call
    // from the driver's resolve pass
    public bool ClaimBlendShape(int index, object owner, int priority)
    {
        if (owner == null || index < 0 || index >= BlendShapeNames.Count)
            return false;
        EnsureClaimArrays();
        var current = _blendShapeOwners![index];
        if (current == null || ReferenceEquals(current, owner) || priority > _blendShapeOwnerPriority![index])
        {
            _blendShapeOwners[index] = owner;
            _blendShapeOwnerPriority![index] = priority;
            return true;
        }
        return false;
    }

    // Gives a shape back, so its synced weight shows again. A driver that stops driving releases what it
    // claimed, or the shape stays frozen at whatever it last wrote and its slider does nothing. -xlinka
    public void ReleaseBlendShape(int index, object owner)
    {
        if (_blendShapeOwners == null || index < 0 || index >= _blendShapeOwners.Length
            || !ReferenceEquals(_blendShapeOwners[index], owner))
            return;
        _blendShapeOwners[index] = null;
        _blendShapeOwnerPriority![index] = 0;
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    // check at drive time so a stolen shape isn't written by the previous owner
    public bool OwnsBlendShape(int index, object owner)
        => _blendShapeOwners != null && index >= 0 && index < _blendShapeOwners.Length
           && ReferenceEquals(_blendShapeOwners[index], owner);

    private void EnsureClaimArrays()
    {
        int n = BlendShapeNames.Count;
        if (_blendShapeOwners == null || _blendShapeOwners.Length != n)
        {
            _blendShapeOwners = new object?[n];
            _blendShapeOwnerPriority = new int[n];
        }
    }

    // local only, no network broadcast; for per-frame animation
    public void DriveBlendShapeWeight(int index, float weight)
    {
        if (index < 0 || index >= BlendShapeNames.Count)
            return;
        if (_runtimeWeights == null || _runtimeWeights.Length != BlendShapeNames.Count)
            System.Array.Resize(ref _runtimeWeights, BlendShapeNames.Count);
        if (_runtimeWeights[index] == weight)
            return;
        _runtimeWeights[index] = weight;
        BlendWeightsChanged = true;
        RunApplyChanges();
    }

    public void DriveBlendShapeWeight(string name, float weight)
        => DriveBlendShapeWeight(GetBlendShapeIndex(name), weight);

    // The driver's value for a shape a driver OWNS, the synced weight for everything else. The
    // override buffer is sized to the whole shape set the first time any driver writes, so reading it
    // for every index handed back a zero for each shape no driver had claimed: on any avatar with a
    // blink driver, every slider that was not a blink did nothing. Ownership is the record of who
    // writes what, so it is the gate. -xlinka
    public float GetEffectiveBlendShapeWeight(int index)
    {
        if (_runtimeWeights != null && _blendShapeOwners != null
            && index >= 0 && index < _runtimeWeights.Length && index < _blendShapeOwners.Length
            && _blendShapeOwners[index] != null)
            return _runtimeWeights[index];
        return (index >= 0 && index < BlendShapeWeights.Count) ? BlendShapeWeights[index] : 0f;
    }

    // called at import; names.Length shapes, each with VertexCount verts
    public void SetBlendShapes(string[]? names, float3[]? flattenedVertices, int mode)
        => SetBlendShapes(names, flattenedVertices, null, mode);

    // Populate blendshapes with optional NORMAL morph deltas (carried when the source provided them, so normals
    // morph with the expression instead of staying frozen at the base). flattenedNormals must line up 1:1 with
    // flattenedVertices or it's ignored. -xlinka
    public void SetBlendShapes(string[]? names, float3[]? flattenedVertices, float3[]? flattenedNormals, int mode)
    {
        BlendShapeNames.Clear();
        BlendShapeVertices.Clear();
        BlendShapeNormals.Clear();
        BlendShapeWeights.Clear();
        // Shape set changed - drop ownership so drivers re-claim against the new indices.
        _blendShapeOwners = null;
        _blendShapeOwnerPriority = null;

        if (names == null || names.Length == 0 || flattenedVertices == null)
            return;

        BlendShapeMode.Value = mode;
        foreach (var n in names)
        {
            BlendShapeNames.Add(n);
            BlendShapeWeights.Add(0f);
        }
        foreach (var v in flattenedVertices)
            BlendShapeVertices.Add(v);

        if (flattenedNormals != null && flattenedNormals.Length == flattenedVertices.Length)
        {
            foreach (var n in flattenedNormals)
                BlendShapeNormals.Add(n);
        }

        MeshDataChanged = true;
        LumoraLogger.Debug($"SkinnedMeshRenderer: Set {names.Length} blendshapes (normals: {(BlendShapeNormals.Count > 0 ? "yes" : "no")})");
    }

    // SETTINGS

    public readonly Sync<ShadowCastMode> ShadowCastMode = new();

    // Single material, kept as the whole-mesh assignment and as what every existing save and every
    // caller already uses.
    public readonly AssetRef<MaterialAsset> Material = new();

    // One material per submesh, for meshes that carry several. A skinned mesh routinely does - a body
    // with a separate face or eye surface is the ordinary case - and with only the single Material
    // above, every surface past the first was painted with the wrong material or none at all. When
    // this list is empty the single Material applies to everything, so nothing that worked before
    // changes. -xlinka
    public readonly SyncAssetList<MaterialAsset> Materials = new();

    // Same contract as MeshRenderer.MaterialsChanged: a list has no was-changed latch of its own, so
    // the hook reads and clears this instead.
    public bool MaterialsChanged { get; set; }

    private readonly LoadingSurfaceLatch _loadingSurfaces = new();

    // The material that governs a given surface: the per-surface entry when the list reaches that far,
    // otherwise the single Material.
    public IAssetProvider<MaterialAsset>? MaterialFor(int surfaceIndex)
    {
        if (surfaceIndex >= 0 && surfaceIndex < Materials.Count)
        {
            var perSurface = Materials[surfaceIndex];
            if (perSurface != null)
                return perSurface;
        }
        return Material.Target;
    }

    public int SurfaceMaterialCount => System.Math.Max(1, Materials.Count);

    // Same contract as MeshRenderer.IsSurfaceLoading: one latch slot per surface, so a mesh whose face
    // material arrives after its body material does not report the whole thing as ready. An avatar
    // arrives mesh-first and textures-later, which is exactly the case this covers. -xlinka
    public bool IsSurfaceLoading(int surfaceIndex = 0)
    {
        var reference = surfaceIndex >= 0 && surfaceIndex < Materials.Count
            ? Materials.GetElement(surfaceIndex) : Material;
        if (!reference.Value.IsNull && reference.Target == null)
            return true;
        return _loadingSurfaces.IsLoading(surfaceIndex, MaterialFor(surfaceIndex));
    }

    public readonly Sync<bool> UpdateWhenOffscreen = new();

    public readonly Sync<SkinQuality> Quality = new();

    // DRAWN ELSEWHERE
    //
    // Part or all of this mesh that something else draws on this machine, a soft body drawing it moving, so this
    // renderer leaves it out: the whole mesh, or single triangles counted over every triangle submesh in order.
    // Leaving the whole out is a visibility switch; leaving triangles out rebuilds the mesh without them. Never
    // replicated: every peer runs its own soft body. -xlinka
    private object? _localHideOwner;

    public bool LocallyHiddenWhole { get; private set; }

    public bool[]? LocallyHiddenTriangles { get; private set; }

    public int LocalHideVersion { get; private set; }

    public void SetLocalHide(object owner, bool whole, bool[]? triangles)
    {
        bool trianglesChanged = !ReferenceEquals(LocallyHiddenTriangles, triangles);
        if (ReferenceEquals(_localHideOwner, owner) && LocallyHiddenWhole == whole && !trianglesChanged)
            return;
        _localHideOwner = owner;
        LocallyHiddenWhole = whole;
        if (trianglesChanged)
        {
            LocallyHiddenTriangles = triangles;
            LocalHideVersion++;
            MeshDataChanged = true;
        }
        RunApplyChanges();
    }

    public void ClearLocalHide(object owner)
    {
        if (!ReferenceEquals(_localHideOwner, owner))
            return;
        _localHideOwner = null;
        bool hadTriangles = LocallyHiddenTriangles != null;
        LocallyHiddenWhole = false;
        LocallyHiddenTriangles = null;
        if (hadTriangles)
        {
            LocalHideVersion++;
            MeshDataChanged = true;
        }
        RunApplyChanges();
    }

    // CHANGE FLAGS

    public bool MeshDataChanged { get; set; }

    public bool SkeletonChanged { get; set; }

    public bool BonesReady => Bones.Count > 0 && Bones[0] != null;

    // set by the hook when binding is complete
    public bool HookBindingComplete { get; set; }

    // Same rule as the other lists above: the element's arrival only reaches us here, so queue the apply.
    private void OnMaterialsChanged(SyncAssetList<MaterialAsset> list)
    {
        MaterialsChanged = true;
        RunApplyChanges();
    }

    private SkeletonBuilder? _watchedSkeleton;

    // Assigning the ref and the skeleton being usable are two different moments: the builder's hook
    // creates the platform skeleton later, and a mesh that tried to bind before that simply never
    // builds. Listen for the ready signal instead of polling for it.
    private void SubscribeToSkeleton()
    {
        var target = Skeleton.Target;
        if (ReferenceEquals(target, _watchedSkeleton))
            return;

        if (_watchedSkeleton != null)
            _watchedSkeleton.SkeletonReady -= OnSkeletonReady;

        _watchedSkeleton = target;
        if (_watchedSkeleton != null)
            _watchedSkeleton.SkeletonReady += OnSkeletonReady;
    }

    private void OnSkeletonReady()
    {
        SkeletonChanged = true;
        RunApplyChanges();
    }

    public override void OnAwake()
    {
        base.OnAwake();
        Materials.OnChanged += OnMaterialsChanged;
        // The mesh draws from under its skeleton, not its own slot, so switching the slot (or anything above
        // it) off hides nothing by itself: the hook has to look again. -xlinka
        Slot.ActiveChanged += OnSlotActiveChanged;

        // Subscribe to changes
        Skeleton.OnChanged += (field) => { SkeletonChanged = true; SubscribeToSkeleton(); RunApplyChanges(); };
        Skeleton.OnTargetChange += OnSkeletonTargetChanged;
        MeshAsset.OnChanged += (field) => { MeshDataChanged = true; RunApplyChanges(); };
        Bones.OnChanged += (list) => { SkeletonChanged = true; RunApplyChanges(); };
        BoneNames.OnChanged += (list) => { SkeletonChanged = true; RunApplyChanges(); };
        Vertices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Normals.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        UVs.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        Indices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneIndices.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BoneWeights.OnChanged += (list) => { MeshDataChanged = true; RunApplyChanges(); };
        BlendShapeWeights.ElementsAdded += (list, index, count) => HookBlendShapeWeightElements(index, count);
        BlendShapeWeights.ElementsRemoving += (list, index, count) => UnhookBlendShapeWeightElements(index, count);
        HookBlendShapeWeightElements(0, BlendShapeWeights.Count);

        LumoraLogger.Debug($"SkinnedMeshRenderer: Awake on slot '{Slot.SlotName.Value}'");
    }

    public override void OnInit()
    {
        base.OnInit();
        ShadowCastMode.Value    = Components.ShadowCastMode.On;
        UpdateWhenOffscreen.Value = true;
        Quality.Value           = SkinQuality.FourBones;
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);

        // Keep re-driving the hook until it has actually applied the mesh (HookBindingComplete, set by the hook
        // once a real surface is built). Covers BOTH paths: the inline/legacy path waits for bones (BonesReady),
        // and the Phos asset path waits for the MeshProvider's async MeshDataAsset decode AND a ready skeleton.
        // The Phos path populates NO Bones, so the old BonesReady-only gate never fired - the asset would finish
        // decoding a few frames later with nothing to re-drive the build, and the mesh stayed invisible. -xlinka
        if (!HookBindingComplete && (BonesReady || MeshAsset.Target != null))
        {
            RunApplyChanges();
        }

        // These flags belong to the hook. A queued apply can be delayed by the frame budget.

        ulong frame = World?.Time.UpdateIndex ?? 0;
        if ((frame + (ulong)_viewStagger) % ViewCheckEvery == 0)
            CheckViewerDistance(frame);
    }

    // OTHER PEOPLE'S AVATARS, FROM HERE
    //
    // Someone else's avatar, or one standing in the world, casts no shadow past the viewer's avatar shadow
    // distance and is not drawn past their draw distance; the wearer's own is never touched, mirrors and all.
    // Worked out every few frames and handed to the hook only when the answer moves. Local to this machine,
    // never replicated. A crowd's shadows were as many triangles again as the crowd. -xlinka
    private const ulong ViewCheckEvery = 8;
    private static int s_viewStaggers;
    private readonly int _viewStagger = System.Threading.Interlocked.Increment(ref s_viewStaggers) & 7;
    private Slot? _viewRoot;
    private ulong _viewRootFrame = ulong.MaxValue;
    private float _appliedShadowDistance = float.NaN;
    private float _appliedDrawDistance = float.NaN;

    // On an avatar the viewer is not wearing.
    public bool LocalOthersAvatar { get; private set; }

    // Past the viewer's avatar shadow distance: the hook drops the shadow whatever ShadowCastMode says.
    public bool LocalShadowCulled { get; private set; }

    // The viewer's avatar draw distance for this renderer, 0 for unlimited.
    public float LocalDrawDistance { get; private set; }

    // Worn by somebody whose shaders this viewer simplifies: the hook swaps toon materials for plain ones.
    public bool LocalSimpleShading { get; private set; }

    private void CheckViewerDistance(ulong frame)
    {
        if (_viewRootFrame == ulong.MaxValue || frame - _viewRootFrame >= 64 || _viewRoot is { IsDestroyed: true })
        {
            _viewRoot = Avatar.AvatarViewLod.RootOf(Slot);
            _viewRootFrame = frame;
        }

        var local = World?.LocalUser?.Root;
        bool others = _viewRoot != null && local != null && !ReferenceEquals(_viewRoot.ActiveUserRoot, local);
        float shadowDistance = EngineSettings.AvatarShadowDistance;
        float drawDistance = others ? EngineSettings.AvatarDrawDistance : 0f;
        bool culled = false;
        if (others && shadowDistance > 0f && local!.HeadSlot is { IsDestroyed: false } head)
        {
            // A little give either side so standing on the line does not flicker the shadow.
            float distance = (_viewRoot!.GlobalPosition - head.GlobalPosition).Length;
            culled = LocalShadowCulled ? distance > shadowDistance - 1f : distance > shadowDistance;
        }

        // Only one somebody is wearing or holding. One standing in the world keeps its shaders: it is usually
        // being looked over before it goes on.
        int shaders = EngineSettings.OthersAvatarShaders;
        bool simple = others && shaders != EngineSettings.OthersShadersFull && _viewRoot!.ActiveUserRoot is { } wearer
            && (shaders == EngineSettings.OthersShadersSimple || wearer.ActiveUser is not { IsFriendOfMine: true });

        if (others == LocalOthersAvatar && culled == LocalShadowCulled && drawDistance == LocalDrawDistance
            && simple == LocalSimpleShading && shadowDistance == _appliedShadowDistance && drawDistance == _appliedDrawDistance)
            return;
        LocalOthersAvatar = others;
        LocalShadowCulled = culled;
        LocalDrawDistance = drawDistance;
        LocalSimpleShading = simple;
        _appliedShadowDistance = shadowDistance;
        _appliedDrawDistance = drawDistance;
        RunApplyChanges();
    }

    private void OnSkeletonTargetChanged(SyncRef<SkeletonBuilder> reference)
    {
        SubscribeToSkeleton();
        SkeletonChanged = true;
        RunApplyChanges();
    }

    private void OnSlotActiveChanged(Slot _) => RunApplyChanges();

    public override void OnDestroy()
    {
        Materials.OnChanged -= OnMaterialsChanged;
        Skeleton.OnTargetChange -= OnSkeletonTargetChanged;
        if (Slot != null)
            Slot.ActiveChanged -= OnSlotActiveChanged;
        if (_watchedSkeleton != null)
        {
            _watchedSkeleton.SkeletonReady -= OnSkeletonReady;
            _watchedSkeleton = null;
        }
        base.OnDestroy();
        LumoraLogger.Debug($"SkinnedMeshRenderer: Destroyed on slot '{Slot?.SlotName.Value}'");
    }

    // BONE SETUP

    // call after mesh data is loaded and bone names are known
    public void SetupBones(Slot rootSlot)
    {
        if (rootSlot == null)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot setup bones with null root slot");
            return;
        }

        if (BoneNames.Count == 0)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: No bone names to setup");
            return;
        }

        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = FindBoneInHierarchy(rootSlot, boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                // Add null reference to maintain bone index alignment
                Bones.Add(null);
                LumoraLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in hierarchy");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        LumoraLogger.Debug($"SkinnedMeshRenderer: Setup {Bones.Count} bones from root '{rootSlot.SlotName.Value}'");
    }

    public void SetupBonesFromSkeleton(SkeletonBuilder skeleton)
    {
        if (skeleton == null || !skeleton.IsBuilt.Value)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot setup bones - skeleton not ready");
            return;
        }

        Skeleton.Target = skeleton;
        Bones.Clear();

        for (int i = 0; i < BoneNames.Count; i++)
        {
            string boneName = BoneNames[i];
            Slot boneSlot = skeleton.GetBoneSlot(boneName);

            if (boneSlot != null)
            {
                Bones.Add(boneSlot);
            }
            else
            {
                Bones.Add(null);
                LumoraLogger.Warn($"SkinnedMeshRenderer: Bone '{boneName}' not found in skeleton");
            }
        }

        SkeletonChanged = true;
        RunApplyChanges();
        LumoraLogger.Debug($"SkinnedMeshRenderer: Setup {Bones.Count} bones from skeleton");
    }

    private static Slot FindBoneInHierarchy(Slot root, string boneName)
    {
        if (root == null || string.IsNullOrEmpty(boneName))
            return null!;

        if (root.SlotName.Value == boneName)
            return root;

        foreach (var child in root.Children)
        {
            var found = FindBoneInHierarchy(child, boneName);
            if (found != null)
                return found;
        }

        return null!;
    }

    // Does this mesh's bone set reach the given slot or anything under it. The explicit Bones list is
    // the authority when the import filled it; a renderer that only names a SkeletonBuilder (the model
    // import path leaves Bones empty) answers from that skeleton's bone slots instead, which may say
    // yes for a bone this particular mesh never weights - the cost of that is one collapse that changes
    // nothing, not a head that stays visible. -xlinka
    public bool BindsBoneUnder(Slot slot)
    {
        if (slot == null || slot.IsDestroyed)
            return false;

        if (Bones.Count > 0)
        {
            foreach (var bone in Bones)
            {
                if (bone != null && !bone.IsDestroyed && (bone == slot || bone.IsDescendantOf(slot)))
                    return true;
            }
            return false;
        }

        var skeleton = Skeleton.Target;
        if (skeleton == null || skeleton.IsDestroyed)
            return false;
        foreach (var bone in skeleton.BoneSlots)
        {
            if (bone != null && !bone.IsDestroyed && (bone == slot || bone.IsDescendantOf(slot)))
                return true;
        }
        return false;
    }

    public Slot GetBone(int index)
    {
        if (index < 0 || index >= Bones.Count)
            return null!;
        return Bones[index]!;
    }

    public Slot GetBone(string name)
    {
        int index = BoneNames.IndexOf(name);
        if (index < 0)
            return null!;
        return GetBone(index);
    }

    // MESH DATA METHODS

    public void SetMeshData(float3[]? vertices, float3[]? normals, float2[]? uvs, int[]? indices,
                            int4[]? boneIndices, float4[]? boneWeights, string[]? boneNames = null)
    {
        if (vertices == null || indices == null)
        {
            LumoraLogger.Warn("SkinnedMeshRenderer: Cannot set mesh data with null vertices or indices");
            return;
        }

        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();
        BoneNames.Clear();

        foreach (var v in vertices)
            Vertices.Add(v);

        if (normals != null)
        {
            foreach (var n in normals)
                Normals.Add(n);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                Normals.Add(new float3(0, 1, 0));
        }

        if (uvs != null)
        {
            foreach (var uv in uvs)
                UVs.Add(uv);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                UVs.Add(new float2(0, 0));
        }

        foreach (var idx in indices)
            Indices.Add(idx);

        if (boneIndices != null)
        {
            foreach (var bi in boneIndices)
                BoneIndices.Add(bi);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneIndices.Add(new int4(0, 0, 0, 0));
        }

        if (boneWeights != null)
        {
            foreach (var bw in boneWeights)
                BoneWeights.Add(bw);
        }
        else
        {
            for (int i = 0; i < vertices.Length; i++)
                BoneWeights.Add(new float4(1, 0, 0, 0));
        }

        if (boneNames != null)
        {
            foreach (var name in boneNames)
                BoneNames.Add(name);
        }

        MeshDataChanged = true;
        LumoraLogger.Debug($"SkinnedMeshRenderer: Set mesh data with {vertices.Length} vertices, {indices.Length / 3} triangles, {boneNames?.Length ?? 0} bones");
    }

    public void ClearMesh()
    {
        Vertices.Clear();
        Normals.Clear();
        UVs.Clear();
        Indices.Clear();
        BoneIndices.Clear();
        BoneWeights.Clear();
        Bones.Clear();
        BoneNames.Clear();
        MeshDataChanged = true;
        LumoraLogger.Debug("SkinnedMeshRenderer: Cleared mesh data");
    }
}

public enum SkinQuality
{
    Auto = 0,
    OneBone = 1,
    TwoBones = 2,
    FourBones = 4
}
