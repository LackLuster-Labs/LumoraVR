using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Components;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for SkinnedMeshRenderer component → Godot MeshInstance3D + Skeleton3D.
/// Creates a deformable mesh that follows a skeleton's bone transforms.
/// Uses bone slot references for proper bone index mapping.
/// </summary>
public class SkinnedMeshHook : ComponentHook<SkinnedMeshRenderer>
{
    private MeshInstance3D _meshInstance;
    private ArrayMesh _arrayMesh;
    private StandardMaterial3D _material;
    private SkeletonHook _skeletonHook;
    private Skeleton3D _skeleton;
    private bool _meshApplied;
    private bool _skeletonBound;

    // Maps mesh bone index → Godot skeleton bone index
    private Dictionary<int, int> _boneIndexMap = new Dictionary<int, int>();

    public override void Initialize()
    {
        base.Initialize();

        // Create MeshInstance3D for rendering
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "SkinnedMeshInstance";

        // Add to scene immediately - will reparent to skeleton later when ready
        attachedNode.AddChild(_meshInstance);
        AquaLogger.Log($"SkinnedMeshHook: Initialized and added mesh to '{Owner.Slot.SlotName.Value}'");

        // Try to bind to skeleton immediately
        TryBindToSkeleton();

        // Apply mesh if data is available
        if (Owner.Vertices.Count > 0)
        {
            ApplyMesh();
        }
    }

    public override void ApplyChanges()
    {
        if (_meshInstance == null)
            return;

        // Try to bind to skeleton if not yet bound OR if we bound but have no bone mappings
        // (skeleton may have been empty when we first tried)
        bool needsBinding = !_skeletonBound || (_skeletonBound && _boneIndexMap.Count == 0 && Owner.BoneNames.Count > 0);
        if (needsBinding)
        {
            TryBindToSkeleton();
        }

        // Rebuild mesh if data changed or not yet applied
        bool shouldApplyMesh = Owner.MeshDataChanged || !_meshApplied;
        if (shouldApplyMesh && Owner.Vertices.Count > 0)
        {
            AquaLogger.Log($"SkinnedMeshHook.ApplyChanges: Applying mesh (changed={Owner.MeshDataChanged}, applied={_meshApplied}, verts={Owner.Vertices.Count})");
            ApplyMesh();
        }

        // Update skeleton reference if changed
        if (Owner.SkeletonChanged)
        {
            _skeletonBound = false;
            _boneIndexMap.Clear();
            TryBindToSkeleton();
        }

        // Update enabled state
        if (GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.Visible = Owner.Enabled;
        }

        // Update material if changed
        if (Owner.Material.GetWasChangedAndClear())
        {
            ApplyMaterial();
        }
    }

    /// <summary>
    /// Apply the component's material or use default fallback.
    /// </summary>
    private void ApplyMaterial()
    {
        if (_meshInstance == null || _arrayMesh == null) return;

        var materialAsset = Owner.Material.Asset;
        if (materialAsset != null && materialAsset.GodotMaterial is Material godotMaterial)
        {
            _meshInstance.SetSurfaceOverrideMaterial(0, godotMaterial);
        }
        else if (_material != null)
        {
            // Use default material
            _meshInstance.SetSurfaceOverrideMaterial(0, _material);
        }
    }

    /// <summary>
    /// Try to bind the mesh instance to the skeleton and build bone index map.
    /// </summary>
    private void TryBindToSkeleton()
    {
        // First try to get skeleton from SkeletonBuilder reference
        if (Owner.Skeleton.Target != null)
        {
            _skeletonHook = Owner.Skeleton.Target.Hook as SkeletonHook;
            if (_skeletonHook != null)
            {
                _skeleton = _skeletonHook.GetSkeleton();
            }
        }

        // If no skeleton yet, try to find one via Bones list
        if (_skeleton == null && Owner.Bones.Count > 0 && Owner.Bones[0] != null)
        {
            // Find skeleton by looking for parent Skeleton3D in the scene
            var boneSlot = Owner.Bones[0];
            var slotHook = boneSlot.Hook as SlotHook;
            if (slotHook != null)
            {
                var node = slotHook.GeneratedNode3D;
                if (node != null)
                {
                    // Walk up to find Skeleton3D
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

        if (_skeleton == null || !GodotObject.IsInstanceValid(_skeleton))
        {
            if (!_meshInstance.IsInsideTree())
            {
                attachedNode.AddChild(_meshInstance);
                AquaLogger.Log("SkinnedMeshHook: No skeleton found - added mesh as static");
            }
            return;
        }

        if (_skeleton.GetBoneCount() == 0)
        {
            if (!_meshInstance.IsInsideTree())
            {
                attachedNode.AddChild(_meshInstance);
                AquaLogger.Log("SkinnedMeshHook: Skeleton has no bones - added mesh as static");
            }
            return;
        }

        // Build bone index mapping from mesh bones to skeleton bones
        BuildBoneIndexMap();

        // Skeleton is ready! Reparent mesh under skeleton if needed
        if (_meshInstance.IsInsideTree())
        {
            if (_meshInstance.GetParent() != _skeleton)
            {
                _meshInstance.GetParent().RemoveChild(_meshInstance);
                _skeleton.AddChild(_meshInstance);
                AquaLogger.Log("SkinnedMeshHook: Reparented mesh under skeleton");
            }
        }
        else
        {
            _skeleton.AddChild(_meshInstance);
            AquaLogger.Log("SkinnedMeshHook: Added mesh as child of skeleton");
        }

        // Set the skeleton path - use ".." since mesh is child of skeleton
        _meshInstance.Skeleton = new NodePath("..");

        _skeletonBound = true;
        AquaLogger.Log($"SkinnedMeshHook: Bound to skeleton '{_skeleton.Name}' with {_skeleton.GetBoneCount()} bones, mapped {_boneIndexMap.Count} mesh bones");

        // Re-apply mesh now that we have skeleton with proper bone mapping
        if (Owner.Vertices.Count > 0)
        {
            _meshApplied = false; // Force re-apply with correct bone indices
            ApplyMesh();
        }

        // Notify component that binding is complete (stops update polling)
        if (_boneIndexMap.Count > 0)
        {
            Owner.HookBindingComplete = true;
        }
    }

    /// <summary>
    /// Build mapping from mesh bone indices to Godot skeleton bone indices.
    /// This is the key to making skinning work correctly.
    /// </summary>
    private void BuildBoneIndexMap()
    {
        _boneIndexMap.Clear();

        if (_skeleton == null)
            return;

        // Method 1: Use BoneNames list if available
        if (Owner.BoneNames.Count > 0)
        {
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.BoneNames.Count; meshBoneIdx++)
            {
                string boneName = Owner.BoneNames[meshBoneIdx];
                int skelBoneIdx = _skeleton.FindBone(boneName);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
                else
                {
                    // Bone not found in skeleton - map to bone 0 as fallback
                    _boneIndexMap[meshBoneIdx] = 0;
                    AquaLogger.Warn($"SkinnedMeshHook: Bone '{boneName}' not found in skeleton, mapping to bone 0");
                }
            }
            AquaLogger.Log($"SkinnedMeshHook: Built bone map from names - {_boneIndexMap.Count} mappings");
            return;
        }

        // Method 2: Use Bones slot references if available
        if (Owner.Bones.Count > 0)
        {
            for (int meshBoneIdx = 0; meshBoneIdx < Owner.Bones.Count; meshBoneIdx++)
            {
                var boneSlot = Owner.Bones[meshBoneIdx];
                if (boneSlot != null)
                {
                    string boneName = boneSlot.SlotName.Value;
                    int skelBoneIdx = _skeleton.FindBone(boneName);

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
            AquaLogger.Log($"SkinnedMeshHook: Built bone map from slots - {_boneIndexMap.Count} mappings");
            return;
        }

        // Method 3: If SkeletonBuilder is available, use its bone order
        if (Owner.Skeleton.Target != null && Owner.Skeleton.Target.BoneNames.Count > 0)
        {
            var skelBuilder = Owner.Skeleton.Target;
            for (int meshBoneIdx = 0; meshBoneIdx < skelBuilder.BoneNames.Count; meshBoneIdx++)
            {
                string boneName = skelBuilder.BoneNames[meshBoneIdx];
                int skelBoneIdx = _skeleton.FindBone(boneName);

                if (skelBoneIdx >= 0)
                {
                    _boneIndexMap[meshBoneIdx] = skelBoneIdx;
                }
            }
            AquaLogger.Log($"SkinnedMeshHook: Built bone map from SkeletonBuilder - {_boneIndexMap.Count} mappings");
            return;
        }

        // Fallback: Assume direct 1:1 mapping
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            _boneIndexMap[i] = i;
        }
        AquaLogger.Log($"SkinnedMeshHook: Using direct bone index mapping (fallback)");
    }

    /// <summary>
    /// Remap a mesh bone index to the corresponding Godot skeleton bone index.
    /// Returns 0 for invalid indices to prevent Godot errors.
    /// </summary>
    private int RemapBoneIndex(int meshBoneIndex)
    {
        // Handle invalid/unused bone indices (like -1)
        if (meshBoneIndex < 0)
            return 0;

        if (_boneIndexMap.TryGetValue(meshBoneIndex, out int skelBoneIndex))
        {
            // Ensure mapped index is valid for skeleton
            int boneCount = _skeleton?.GetBoneCount() ?? 0;
            if (skelBoneIndex < 0 || skelBoneIndex >= boneCount)
                return 0;
            return skelBoneIndex;
        }

        // Fallback: clamp to valid skeleton bone range
        int maxBone = (_skeleton?.GetBoneCount() ?? 1) - 1;
        return System.Math.Clamp(meshBoneIndex, 0, System.Math.Max(0, maxBone));
    }

    /// <summary>
    /// Build and apply the mesh from component data.
    /// </summary>
    private void ApplyMesh()
    {
        if (_meshInstance == null)
            return;

        // Check if we have valid mesh data
        if (Owner.Vertices.Count == 0 || Owner.Indices.Count == 0)
        {
            AquaLogger.Log("SkinnedMeshHook: No mesh data");
            _meshInstance.Mesh = null;
            _meshApplied = false;
            return;
        }

        // Create ArrayMesh
        _arrayMesh?.Dispose();
        _arrayMesh = new ArrayMesh();

        // Build arrays for Godot
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
        if (Owner.Normals.Count == Owner.Vertices.Count)
        {
            var normals = new Vector3[Owner.Normals.Count];
            for (int i = 0; i < Owner.Normals.Count; i++)
            {
                var n = Owner.Normals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // UVs
        if (Owner.UVs.Count == Owner.Vertices.Count)
        {
            var uvs = new Vector2[Owner.UVs.Count];
            for (int i = 0; i < Owner.UVs.Count; i++)
            {
                var uv = Owner.UVs[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        // Indices
        var indices = new int[Owner.Indices.Count];
        for (int i = 0; i < Owner.Indices.Count; i++)
        {
            indices[i] = Owner.Indices[i];
        }
        arrays[(int)Mesh.ArrayType.Index] = indices;

        // Bone weights and indices for skinning
        // IMPORTANT: Only add bone data if we have valid bone mappings AND skeleton is ready
        // Otherwise Godot will error with invalid bone indices
        bool hasBoneData = Owner.BoneIndices.Count == Owner.Vertices.Count &&
                           Owner.BoneWeights.Count == Owner.Vertices.Count;
        bool hasValidMappings = _boneIndexMap.Count > 0 && _skeleton != null && _skeleton.GetBoneCount() > 0;

        if (hasBoneData && hasValidMappings)
        {
            // Godot expects bone indices as int array (4 per vertex)
            // We need to remap mesh bone indices to Godot skeleton bone indices
            var boneIndices = new int[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneIndices.Count; i++)
            {
                var bi = Owner.BoneIndices[i];
                // Remap each bone index to the Godot skeleton's bone index
                boneIndices[i * 4 + 0] = RemapBoneIndex(bi.x);
                boneIndices[i * 4 + 1] = RemapBoneIndex(bi.y);
                boneIndices[i * 4 + 2] = RemapBoneIndex(bi.z);
                boneIndices[i * 4 + 3] = RemapBoneIndex(bi.w);
            }
            arrays[(int)Mesh.ArrayType.Bones] = boneIndices;

            // Godot expects bone weights as float array (4 per vertex)
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

            AquaLogger.Log($"SkinnedMeshHook: Added bone data for {Owner.Vertices.Count} vertices with {_boneIndexMap.Count} bone mappings");
        }
        else if (hasBoneData)
        {
            AquaLogger.Log($"SkinnedMeshHook: Skipping bone data - skeleton not ready (mappings={_boneIndexMap.Count}, skelBones={_skeleton?.GetBoneCount() ?? 0})");
        }
        else
        {
            AquaLogger.Warn($"SkinnedMeshHook: Missing bone data - indices:{Owner.BoneIndices.Count} weights:{Owner.BoneWeights.Count} verts:{Owner.Vertices.Count}");
        }

        // Add surface to mesh
        _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // Set mesh on instance
        _meshInstance.Mesh = _arrayMesh;

        // Apply material - use component's material if set, otherwise use default
        var materialAsset = Owner.Material.Asset;
        if (materialAsset != null && materialAsset.GodotMaterial is Material godotMaterial)
        {
            _meshInstance.SetSurfaceOverrideMaterial(0, godotMaterial);
        }
        else
        {
            // Create/apply default material
            if (_material == null)
            {
                _material = new StandardMaterial3D();
                _material.AlbedoColor = new Color(0.8f, 0.7f, 0.6f); // Skin-like color
                _material.Roughness = 0.8f;
                _material.Metallic = 0.0f;
                _material.CullMode = BaseMaterial3D.CullModeEnum.Back;
            }
            _meshInstance.SetSurfaceOverrideMaterial(0, _material);
        }

        _meshApplied = true;
        AquaLogger.Log($"SkinnedMeshHook: Applied mesh with {Owner.Vertices.Count} vertices, {Owner.Indices.Count / 3} triangles");
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.QueueFree();
        }

        _arrayMesh?.Dispose();
        _material?.Dispose();

        _meshInstance = null;
        _arrayMesh = null;
        _material = null;
        _skeletonHook = null;
        _skeleton = null;
        _boneIndexMap.Clear();

        base.Destroy(destroyingWorld);
    }
}
