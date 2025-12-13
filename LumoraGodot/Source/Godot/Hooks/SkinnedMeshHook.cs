
using Godot;
using Lumora.Core.Components;
using Lumora.Core.Math;
using System.Collections.Generic;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Hook for SkinnedMeshRenderer component → Godot MeshInstance3D + Skeleton3D.
/// Creates a deformable mesh that follows a skeleton's bone transforms.
/// Platform skinned mesh hook for Godot.
/// </summary>
public class SkinnedMeshHook : ComponentHook<SkinnedMeshRenderer>
{
    private MeshInstance3D _meshInstance;
    private ArrayMesh _arrayMesh;
    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private SkeletonHook _skeletonHook;

    public override void Initialize()
    {
        base.Initialize();

        // Create MeshInstance3D for rendering
        _meshInstance = new MeshInstance3D();
        _meshInstance.Name = "SkinnedMesh";
        attachedNode.AddChild(_meshInstance);

        // Apply initial mesh and skeleton
        UpdateSkeletonReference();
        ApplyMesh();

        AquaLogger.Log($"SkinnedMeshHook: Initialized for slot '{Owner.Slot.SlotName.Value}'");
    }

    public override void ApplyChanges()
    {
        if (_meshInstance == null || !GodotObject.IsInstanceValid(_meshInstance))
            return;

        // Update skeleton reference if changed
        if (Owner.SkeletonChanged)
        {
            UpdateSkeletonReference();
        }

        // Rebuild mesh if data changed
        if (Owner.MeshDataChanged)
        {
            ApplyMesh();
        }

        // Update enabled state
        bool enabled = Owner.Enabled;
        if (_meshInstance.Visible != enabled)
        {
            _meshInstance.Visible = enabled;
        }
    }

    /// <summary>
    /// Update the skeleton reference and attach mesh to skeleton.
    /// </summary>
    private void UpdateSkeletonReference()
    {
        _skeletonHook = null;

        if (Owner.Skeleton.Target == null)
        {
            AquaLogger.Log("SkinnedMeshHook: No skeleton reference");
            return;
        }

        // Get the SkeletonHook from the SkeletonBuilder component
        var skeletonComponent = Owner.Skeleton.Target;
        if (skeletonComponent.Hook is SkeletonHook hook)
        {
            _skeletonHook = hook;

            // Get the Skeleton3D node
            var skeleton = _skeletonHook.GetSkeleton();
            if (skeleton != null && GodotObject.IsInstanceValid(skeleton))
            {
                // Set the skeleton path on the mesh instance
                _meshInstance.Skeleton = _meshInstance.GetPathTo(skeleton);

                AquaLogger.Log($"SkinnedMeshHook: Attached to skeleton '{skeleton.Name}' with {skeleton.GetBoneCount()} bones");
            }
        }
        else
        {
            AquaLogger.Warn("SkinnedMeshHook: Skeleton component does not have a SkeletonHook");
        }
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
            AquaLogger.Log("SkinnedMeshHook: No mesh data, clearing mesh");
            _meshInstance.Mesh = null;
            return;
        }

        // Create ArrayMesh
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

        // Bone weights and indices
        if (Owner.BoneIndices.Count == Owner.Vertices.Count &&
            Owner.BoneWeights.Count == Owner.Vertices.Count)
        {
            // Godot expects bone indices as int array (4 per vertex)
            var boneIndices = new int[Owner.Vertices.Count * 4];
            for (int i = 0; i < Owner.BoneIndices.Count; i++)
            {
                var bi = Owner.BoneIndices[i];
                boneIndices[i * 4 + 0] = bi.x;
                boneIndices[i * 4 + 1] = bi.y;
                boneIndices[i * 4 + 2] = bi.z;
                boneIndices[i * 4 + 3] = bi.w;
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
        }

        // Add surface to mesh
        _arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // Set mesh on instance
        _meshInstance.Mesh = _arrayMesh;

        // Apply materials
        ApplyMaterials();

        AquaLogger.Log($"SkinnedMeshHook: Applied mesh with {Owner.Vertices.Count} vertices, {Owner.Indices.Count / 3} triangles");
    }

    /// <summary>
    /// Apply materials to the mesh.
    /// </summary>
    private void ApplyMaterials()
    {
        if (_meshInstance == null || _arrayMesh == null)
            return;

        // For now, create a simple default material
        // TODO: Properly convert LumoraMaterial to Godot Material when material system is implemented

        for (int i = 0; i < _arrayMesh.GetSurfaceCount(); i++)
        {
            if (!_materials.ContainsKey(i))
            {
                var material = new StandardMaterial3D();
                material.AlbedoColor = new Color(0.9f, 0.9f, 0.9f); // Light gray
                material.MetallicSpecular = 0.3f;
                material.Roughness = 0.7f;

                // Enable skinning
                // Note: Godot automatically handles skinning when bone data is present

                _materials[i] = material;
            }

            _meshInstance.SetSurfaceOverrideMaterial(i, _materials[i]);
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        if (!destroyingWorld && _meshInstance != null && GodotObject.IsInstanceValid(_meshInstance))
        {
            _meshInstance.QueueFree();
        }

        if (_arrayMesh != null)
        {
            _arrayMesh.Dispose();
        }

        _meshInstance = null;
        _arrayMesh = null;
        _materials.Clear();
        _skeletonHook = null;

        base.Destroy(destroyingWorld);
    }
}
