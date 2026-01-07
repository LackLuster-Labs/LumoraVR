using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Phos;
using Lumora.Core.Math;
using SharpGLTF.Schema2;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Component that provides mesh assets from URLs.
/// Loads mesh files and creates MeshDataAsset instances.
/// </summary>
public class MeshProvider : UrlAssetProvider<MeshDataAsset, MeshDataMetadata>
{
    /// <summary>
    /// Whether to keep mesh data readable after GPU upload.
    /// Useful for physics collisions or runtime mesh manipulation.
    /// </summary>
    public readonly Sync<bool> KeepReadable;

    /// <summary>
    /// Scale factor to apply when loading the mesh.
    /// </summary>
    public readonly Sync<float> ImportScale;

    public MeshProvider()
    {
        KeepReadable = new Sync<bool>(this, false);
        ImportScale = new Sync<float>(this, 1.0f);
    }

    protected override async Task<MeshDataMetadata> LoadMetadata(Uri url, CancellationToken token)
    {
        // Read mesh header to estimate size without full load
        var metadata = new MeshDataMetadata();
        long fileLength = 0;
        string ext = "";

        if (url.IsFile)
        {
            var fileInfo = new FileInfo(url.LocalPath);
            fileLength = fileInfo.Length;
            ext = fileInfo.Extension.ToLowerInvariant();
        }
        else if (url.Scheme is "http" or "https")
        {
            // For HTTP URLs, get extension from path
            ext = Path.GetExtension(url.AbsolutePath)?.ToLowerInvariant() ?? "";

            // Prefetch content to cache and get size
            var contentCache = Engine.Current?.ContentCache;
            if (contentCache != null)
            {
                var data = await contentCache.Get(url, token);
                fileLength = data?.Length ?? 0;
            }
        }

        if (fileLength > 0)
        {
            // Rough estimates based on file format
            switch (ext)
            {
                case ".obj":
                    // OBJ files are text, ~100 bytes per vertex estimate
                    metadata.VertexCount = (int)(fileLength / 100);
                    metadata.TriangleCount = metadata.VertexCount / 3;
                    break;
                case ".glb":
                case ".gltf":
                    // GLB is binary, ~50 bytes per vertex estimate
                    metadata.VertexCount = (int)(fileLength / 50);
                    metadata.TriangleCount = metadata.VertexCount / 3;
                    break;
                case ".fbx":
                    // FBX varies, ~80 bytes per vertex estimate
                    metadata.VertexCount = (int)(fileLength / 80);
                    metadata.TriangleCount = metadata.VertexCount / 3;
                    break;
                default:
                    // Unknown format, rough estimate
                    metadata.VertexCount = (int)(fileLength / 64);
                    metadata.TriangleCount = metadata.VertexCount / 3;
                    break;
            }

            metadata.HasUV = true;
            metadata.HasNormals = true;
        }

        return metadata;
    }

    protected override async Task<MeshDataAsset> LoadAssetData(Uri url, MeshDataMetadata metadata, CancellationToken token)
    {
        byte[] fileData = await LoadFileBytes(url, token);
        if (token.IsCancellationRequested) return null;

        PhosMesh mesh = DecodeMesh(fileData, url);
        if (mesh == null) return null;

        // Apply import scale if not 1.0
        float scale = ImportScale.Value;
        if (System.Math.Abs(scale - 1.0f) > 0.0001f)
        {
            ScaleMesh(mesh, scale);
        }

        var asset = new MeshDataAsset();
        asset.KeepReadable = KeepReadable.Value;
        asset.SetMeshData(mesh);
        return asset;
    }

    private async Task<byte[]> LoadFileBytes(Uri url, CancellationToken token)
    {
        if (url.IsFile)
        {
            return await File.ReadAllBytesAsync(url.LocalPath, token);
        }

        // For HTTP/HTTPS/lumora URLs, use ContentCache
        if (url.Scheme is "http" or "https" or "lumora")
        {
            var contentCache = Engine.Current?.ContentCache;
            if (contentCache != null)
            {
                var data = await contentCache.Get(url, token);
                if (data != null)
                {
                    return data;
                }
            }
            throw new InvalidOperationException($"Failed to load content from URL: {url}");
        }

        throw new NotSupportedException($"URL scheme not supported: {url.Scheme}");
    }

    private PhosMesh DecodeMesh(byte[] fileData, Uri url)
    {
        if (fileData == null || fileData.Length == 0)
        {
            AquaLogger.Warn("MeshProvider: Empty file data");
            return null;
        }

        string ext = Path.GetExtension(url.AbsolutePath)?.ToLowerInvariant() ?? "";

        try
        {
            return ext switch
            {
                ".glb" or ".gltf" => DecodeGltf(fileData),
                ".obj" => DecodeObj(fileData),
                _ => throw new NotSupportedException($"Unsupported mesh format: {ext}")
            };
        }
        catch (Exception ex)
        {
            AquaLogger.Error($"MeshProvider: Failed to decode mesh - {ex.Message}");
            return null;
        }
    }

    private PhosMesh DecodeGltf(byte[] fileData)
    {
        using var stream = new MemoryStream(fileData);
        var model = ModelRoot.ReadGLB(stream);

        var phosMesh = new PhosMesh();
        phosMesh.HasNormals = true;
        phosMesh.HasUV0s = true;

        // Collect all primitives from all meshes
        var allPositions = new List<float3>();
        var allNormals = new List<float3>();
        var allUVs = new List<float2>();
        var allIndices = new List<int>();
        int vertexOffset = 0;

        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var posAccessor = primitive.GetVertexAccessor("POSITION");
                var normAccessor = primitive.GetVertexAccessor("NORMAL");
                var uvAccessor = primitive.GetVertexAccessor("TEXCOORD_0");
                var indexAccessor = primitive.IndexAccessor;

                if (posAccessor == null) continue;

                var positions = posAccessor.AsVector3Array();
                var normals = normAccessor?.AsVector3Array();
                var uvs = uvAccessor?.AsVector2Array();

                foreach (var pos in positions)
                {
                    allPositions.Add(new float3(pos.X, pos.Y, pos.Z));
                }

                if (normals != null)
                {
                    foreach (var norm in normals)
                    {
                        allNormals.Add(new float3(norm.X, norm.Y, norm.Z));
                    }
                }
                else
                {
                    for (int i = 0; i < positions.Count; i++)
                        allNormals.Add(float3.Up);
                }

                if (uvs != null)
                {
                    foreach (var uv in uvs)
                    {
                        allUVs.Add(new float2(uv.X, uv.Y));
                    }
                }
                else
                {
                    for (int i = 0; i < positions.Count; i++)
                        allUVs.Add(float2.Zero);
                }

                if (indexAccessor != null)
                {
                    var indices = indexAccessor.AsIndicesArray();
                    foreach (var idx in indices)
                    {
                        allIndices.Add((int)idx + vertexOffset);
                    }
                }

                vertexOffset += positions.Count;
            }
        }

        if (allPositions.Count == 0)
        {
            AquaLogger.Warn("MeshProvider: GLTF has no vertex data");
            return phosMesh;
        }

        // Populate PhosMesh
        phosMesh.IncreaseVertexCount(allPositions.Count);

        for (int i = 0; i < allPositions.Count; i++)
        {
            phosMesh.positions[i] = allPositions[i];
            phosMesh.normals[i] = allNormals[i];
        }

        // Set UVs
        phosMesh.SetHasUV(0, true);
        for (int i = 0; i < allUVs.Count; i++)
        {
            phosMesh.SetUV(0, i, allUVs[i]);
        }

        // Create submesh with triangles
        var submesh = new PhosTriangleSubmesh(phosMesh);
        phosMesh.Submeshes.Add(submesh);

        for (int i = 0; i < allIndices.Count; i += 3)
        {
            if (i + 2 < allIndices.Count)
            {
                submesh.AddTriangle(allIndices[i], allIndices[i + 1], allIndices[i + 2]);
            }
        }

        AquaLogger.Debug($"MeshProvider: Decoded GLTF with {allPositions.Count} vertices, {allIndices.Count / 3} triangles");
        return phosMesh;
    }

    private PhosMesh DecodeObj(byte[] fileData)
    {
        var text = System.Text.Encoding.UTF8.GetString(fileData);
        var lines = text.Split('\n');

        var positions = new List<float3>();
        var normals = new List<float3>();
        var uvs = new List<float2>();
        var faceIndices = new List<(int v, int vt, int vn)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    positions.Add(new float3(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "vn" when parts.Length >= 4:
                    normals.Add(new float3(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "vt" when parts.Length >= 3:
                    uvs.Add(new float2(
                        float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)));
                    break;

                case "f" when parts.Length >= 4:
                    // Parse face (triangulate if needed)
                    var faceVerts = new List<(int v, int vt, int vn)>();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        faceVerts.Add(ParseObjFaceVertex(parts[i]));
                    }
                    // Triangulate (fan triangulation)
                    for (int i = 1; i < faceVerts.Count - 1; i++)
                    {
                        faceIndices.Add(faceVerts[0]);
                        faceIndices.Add(faceVerts[i]);
                        faceIndices.Add(faceVerts[i + 1]);
                    }
                    break;
            }
        }

        // Build PhosMesh with unique vertices
        var phosMesh = new PhosMesh();
        phosMesh.HasNormals = normals.Count > 0;
        phosMesh.HasUV0s = uvs.Count > 0;

        phosMesh.IncreaseVertexCount(faceIndices.Count);

        for (int i = 0; i < faceIndices.Count; i++)
        {
            var (v, vt, vn) = faceIndices[i];

            phosMesh.positions[i] = v > 0 && v <= positions.Count ? positions[v - 1] : float3.Zero;

            if (phosMesh.HasNormals)
                phosMesh.normals[i] = vn > 0 && vn <= normals.Count ? normals[vn - 1] : float3.Up;
        }

        if (phosMesh.HasUV0s)
        {
            phosMesh.SetHasUV(0, true);
            for (int i = 0; i < faceIndices.Count; i++)
            {
                var vt = faceIndices[i].vt;
                var uv = vt > 0 && vt <= uvs.Count ? uvs[vt - 1] : float2.Zero;
                phosMesh.SetUV(0, i, uv);
            }
        }

        // Create submesh
        var submesh = new PhosTriangleSubmesh(phosMesh);
        phosMesh.Submeshes.Add(submesh);

        for (int i = 0; i < faceIndices.Count; i += 3)
        {
            submesh.AddTriangle(i, i + 1, i + 2);
        }

        AquaLogger.Debug($"MeshProvider: Decoded OBJ with {faceIndices.Count} vertices, {faceIndices.Count / 3} triangles");
        return phosMesh;
    }

    private (int v, int vt, int vn) ParseObjFaceVertex(string vertex)
    {
        var parts = vertex.Split('/');
        int v = 0, vt = 0, vn = 0;

        if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
            int.TryParse(parts[0], out v);
        if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
            int.TryParse(parts[1], out vt);
        if (parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]))
            int.TryParse(parts[2], out vn);

        return (v, vt, vn);
    }

    private void ScaleMesh(PhosMesh mesh, float scale)
    {
        // Scale all vertex positions
        var positions = mesh.RawPositions;
        if (positions != null && positions.Length > 0)
        {
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] *= scale;
            }
        }
    }
}
