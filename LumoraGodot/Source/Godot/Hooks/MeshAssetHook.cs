using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Phos;

namespace Aquamarine.Godot.Hooks;

/// <summary>
/// Godot implementation of mesh asset hook.
/// Creates and manages Godot ArrayMesh resources.
/// </summary>
public class MeshAssetHook : AssetHook, IMeshAssetHook
{
    private ArrayMesh _godotMesh;

    /// <summary>
    /// Get the Godot ArrayMesh.
    /// </summary>
    public ArrayMesh GodotMesh => _godotMesh;

    /// <summary>
    /// Whether the mesh is valid and usable.
    /// </summary>
    public bool IsValid => _godotMesh != null;

    /// <summary>
    /// Upload PhosMesh data to the Godot mesh.
    /// </summary>
    public void UploadMesh(PhosMesh mesh)
    {
        if (mesh == null || mesh.VertexCount == 0) return;

        // Create new mesh if needed
        if (_godotMesh == null)
        {
            _godotMesh = new ArrayMesh();
        }
        else
        {
            // Clear existing surfaces
            _godotMesh.ClearSurfaces();
        }

        // Process each submesh as a separate surface
        foreach (var submesh in mesh.Submeshes)
        {
            if (submesh.IndexCount == 0) continue;

            var arrays = BuildSurfaceArrays(mesh, submesh);
            if (arrays != null)
            {
                _godotMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
        }

        // If no submeshes but we have vertex data, create a single surface
        if (mesh.Submeshes.Count == 0 && mesh.VertexCount > 0)
        {
            var arrays = BuildSurfaceArraysNoIndices(mesh);
            if (arrays != null)
            {
                if ((mesh.VertexCount % 3) != 0)
                {
                    Lumora.Core.Logging.Logger.Warn($"MeshAssetHook.UploadMesh: Skipping surface - no indices and vertex count {mesh.VertexCount} is not a multiple of 3");
                    return;
                }
                _godotMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
        }
    }

    private global::Godot.Collections.Array BuildSurfaceArrays(PhosMesh mesh, PhosSubmesh submesh)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        int vertexCount = mesh.VertexCount;

        // Positions (required)
        var rawPositions = mesh.RawPositions;
        if (rawPositions != null && rawPositions.Length > 0)
        {
            var positions = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var pos = rawPositions[i];
                positions[i] = new Vector3(pos.x, pos.y, pos.z);
            }
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
        }

        // Normals
        var rawNormals = mesh.RawNormals;
        if (rawNormals != null && rawNormals.Length > 0)
        {
            var normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var normal = rawNormals[i];
                normals[i] = new Vector3(normal.x, normal.y, normal.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // Tangents
        var rawTangents = mesh.RawTangents;
        if (rawTangents != null && rawTangents.Length > 0)
        {
            var tangents = new float[vertexCount * 4];
            for (int i = 0; i < vertexCount; i++)
            {
                var t = rawTangents[i];
                tangents[i * 4] = t.x;
                tangents[i * 4 + 1] = t.y;
                tangents[i * 4 + 2] = t.z;
                tangents[i * 4 + 3] = t.w;
            }
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
        }

        // UV0
        var rawUV0 = mesh.RawUV0s;
        if (rawUV0 != null && rawUV0.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = rawUV0[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        // UV1
        var rawUV1 = mesh.RawUV1s;
        if (rawUV1 != null && rawUV1.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = rawUV1[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV2] = uvs;
        }

        // Vertex colors
        var rawColors = mesh.RawColors;
        if (rawColors != null && rawColors.Length > 0)
        {
            var colors = new Color[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var c = rawColors[i];
                colors[i] = new Color(c.r, c.g, c.b, c.a);
            }
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }

        // Indices from submesh
        var rawIndices = submesh.RawIndices;
        if (rawIndices != null && rawIndices.Length > 0)
        {
            arrays[(int)Mesh.ArrayType.Index] = rawIndices;
        }

        return arrays;
    }

    private global::Godot.Collections.Array BuildSurfaceArraysNoIndices(PhosMesh mesh)
    {
        var arrays = new global::Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);

        int vertexCount = mesh.VertexCount;

        // Positions (required)
        var rawPositions = mesh.RawPositions;
        if (rawPositions == null || rawPositions.Length == 0) return null;

        var positions = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            var pos = rawPositions[i];
            positions[i] = new Vector3(pos.x, pos.y, pos.z);
        }
        arrays[(int)Mesh.ArrayType.Vertex] = positions;

        // Normals
        var rawNormals = mesh.RawNormals;
        if (rawNormals != null && rawNormals.Length > 0)
        {
            var normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var normal = rawNormals[i];
                normals[i] = new Vector3(normal.x, normal.y, normal.z);
            }
            arrays[(int)Mesh.ArrayType.Normal] = normals;
        }

        // UV0
        var rawUV0 = mesh.RawUV0s;
        if (rawUV0 != null && rawUV0.Length > 0)
        {
            var uvs = new Vector2[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var uv = rawUV0[i];
                uvs[i] = new Vector2(uv.x, uv.y);
            }
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        return arrays;
    }

    /// <summary>
    /// Unload and dispose the Godot mesh.
    /// </summary>
    public override void Unload()
    {
        if (_godotMesh != null)
        {
            _godotMesh.Dispose();
            _godotMesh = null;
        }
    }
}
