using Godot;
using Lumora.Core;
using Lumora.Core.Components.Meshes;
using Lumora.Core.Phos;
using Lumora.Core.Logging;
using System;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Godot hook for ProceduralMesh components.
/// Converts PhosMesh to Godot ArrayMesh and uploads to GPU.
/// Platform mesh hook for Godot.
/// </summary>
#nullable enable
public class MeshHook : ComponentHook<ProceduralMesh>
{
    private ArrayMesh? godotMesh;
    private MeshInstance3D? meshInstance;
    private SlotHook? _meshSlotHook;
    private Node3D? parentNode;

    /// <summary>
    /// Factory method for creating mesh hooks.
    /// </summary>
    public static IHook<ProceduralMesh> Constructor()
    {
        return new MeshHook();
    }

    public override void Initialize()
    {
        Lumora.Core.Logging.Logger.Log($"MeshHook.Initialize: Starting for component on slot '{Owner?.Slot?.SlotName?.Value}'");

        // Create the ArrayMesh to hold mesh data
        godotMesh = new ArrayMesh();

        // Create Godot mesh instance (will hide if MeshRenderer is present)
        meshInstance = new MeshInstance3D();
        meshInstance.Name = "PhosMesh";
        meshInstance.Mesh = godotMesh;

        // Create default material so mesh is visible
        var material = new StandardMaterial3D();
        material.AlbedoColor = new Color(0.8f, 0.8f, 0.8f); // Light gray
        material.Roughness = 0.7f;
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel;

        var slotName = Owner?.Slot?.SlotName?.Value;
        if (!string.IsNullOrEmpty(slotName) && slotName.StartsWith("Default", System.StringComparison.Ordinal))
        {
            material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        meshInstance.MaterialOverride = material;

        // Get slot hook and request Node3D
        if (Owner.Slot?.Hook is SlotHook hook)
        {
            _meshSlotHook = hook;
            parentNode = _meshSlotHook.RequestNode3D();
            parentNode.AddChild(meshInstance);
            Lumora.Core.Logging.Logger.Log($"MeshHook: Successfully added mesh to slot '{Owner.Slot.SlotName.Value}'");
        }
        else
        {
            Lumora.Core.Logging.Logger.Warn($"MeshHook: Slot hook not found for '{Owner.Slot?.SlotName.Value}'");
        }

        // Initial upload if mesh data is ready
        if (Owner.PhosMesh != null)
        {
            UploadMesh(Owner.PhosMesh, Owner.UploadHint);
        }
    }

    public override void ApplyChanges()
    {
        // Upload changes if mesh is dirty
        if (Owner.IsDirty && Owner.PhosMesh != null)
        {
            UploadMesh(Owner.PhosMesh, Owner.UploadHint);
            Owner.ClearDirty();
        }

        // Hide our MeshInstance3D if a MeshRenderer is handling rendering
        // MeshRenderer creates its own MeshInstance3D with its own material
        if (meshInstance != null)
        {
            bool hasMeshRenderer = Owner.Slot?.GetComponent<Lumora.Core.Components.MeshRenderer>() != null;
            if (meshInstance.Visible != !hasMeshRenderer)
            {
                meshInstance.Visible = !hasMeshRenderer;
            }
        }
    }

    public override void Destroy(bool destroyingWorld)
    {
        // Clean up Godot resources
        if (!destroyingWorld)
        {
            // Free the Node3D request from slot hook
            _meshSlotHook?.FreeNode3D();

            if (meshInstance != null && GodotObject.IsInstanceValid(meshInstance))
            {
                meshInstance.QueueFree();
            }

            if (godotMesh != null)
            {
                godotMesh.Dispose();
            }
        }

        meshInstance = null;
        godotMesh = null;
        _meshSlotHook = null;
        parentNode = null;
    }

    /// <summary>
    /// Upload PhosMesh to Godot ArrayMesh.
    /// Only uploads channels marked dirty in the upload hint.
    /// </summary>
    private void UploadMesh(PhosMesh phosMesh, MeshUploadHint uploadHint)
    {
        if (godotMesh == null) return;

        // Lumora.Core.Logging.Logger.Log($"MeshHook.UploadMesh: Uploading mesh with {phosMesh.VertexCount} vertices");

        // Clear existing surfaces
        godotMesh.ClearSurfaces();

        // Upload each submesh as a surface
        foreach (var submesh in phosMesh.Submeshes)
        {
            if (submesh is PhosTriangleSubmesh triangleSubmesh)
            {
                UploadTriangleSubmesh(phosMesh, triangleSubmesh, uploadHint);
            }
        }
    }

    /// <summary>
    /// Upload a triangle submesh to Godot.
    /// </summary>
    private void UploadTriangleSubmesh(PhosMesh phosMesh, PhosTriangleSubmesh submesh, MeshUploadHint uploadHint)
    {
        if (godotMesh == null) return;

        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        // Upload positions (ALWAYS required, regardless of hint)
        if (phosMesh.VertexCount > 0 && phosMesh.RawPositions != null)
        {
            var positions = new Vector3[phosMesh.VertexCount];
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var pos = phosMesh.RawPositions[i];
                positions[i] = new Vector3(pos.x, pos.y, pos.z);
            }
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
        }

        // Upload normals
        if (phosMesh.HasNormals && uploadHint[MeshUploadHint.Flag.Normals])
        {
            var normals = new Vector3[phosMesh.VertexCount];
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var n = phosMesh.RawNormals[i];
                normals[i] = new Vector3(n.x, n.y, n.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // Upload tangents
        if (phosMesh.HasTangents && uploadHint[MeshUploadHint.Flag.Tangents])
        {
            var tangents = new float[phosMesh.VertexCount * 4];
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var t = phosMesh.RawTangents[i];
                tangents[i * 4 + 0] = t.x;
                tangents[i * 4 + 1] = t.y;
                tangents[i * 4 + 2] = t.z;
                tangents[i * 4 + 3] = t.w;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        }

        // Upload colors
        if (phosMesh.HasColors && uploadHint[MeshUploadHint.Flag.Colors])
        {
            var colors = new Color[phosMesh.VertexCount];
            var rawColors = phosMesh.RawColors;
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                // Safety: use white if color array is undersized
                if (i < rawColors.Length)
                {
                    var c = rawColors[i];
                    colors[i] = new Color(c.r, c.g, c.b, c.a);
                }
                else
                {
                    colors[i] = Colors.White;
                }
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }

        // Upload UV0
        if (phosMesh.HasUV0s && uploadHint[MeshUploadHint.Flag.UV0])
        {
            var uvs = new Vector2[phosMesh.VertexCount];
            for (int i = 0; i < phosMesh.VertexCount; i++)
            {
                var uv = phosMesh.RawUV0s[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        // Upload indices (ALWAYS required for triangle meshes)
        bool hasIndices = submesh.IndexCount > 0 && submesh.RawIndices != null;
        if (hasIndices)
        {
            var indices = new int[submesh.IndexCount];
            for (int i = 0; i < submesh.IndexCount; i++)
            {
                indices[i] = submesh.RawIndices[i];
            }
            arrays[(int)Mesh.ArrayType.Index] = indices;
        }

        // Only add surface if we have valid vertex data
        var vertexArray = arrays[(int)Mesh.ArrayType.Vertex];
        if (vertexArray.VariantType != Variant.Type.Nil && vertexArray.AsVector3Array() != null)
        {
            if (!hasIndices && (phosMesh.VertexCount % 3) != 0)
            {
                Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no indices and vertex count {phosMesh.VertexCount} is not a multiple of 3");
                return;
            }
            godotMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            // Lumora.Core.Logging.Logger.Log($"MeshHook.UploadTriangleSubmesh: Uploaded {submesh.IndexCount / 3} triangles");
        }
        else
        {
            Lumora.Core.Logging.Logger.Warn($"MeshHook.UploadTriangleSubmesh: Skipping surface - no vertex data");
        }
    }

    /// <summary>
    /// Get the Godot MeshInstance3D node.
    /// </summary>
    public MeshInstance3D? GetMeshInstance() => meshInstance;
}
