using System;
using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Assets;

/// <summary>
/// Asset containing mesh geometry data.
/// Supports both static (loaded) and dynamic (procedural) meshes.
/// </summary>
public class MeshDataAsset : DynamicImplementableAsset<IMeshAssetHook>
{
    private PhosMesh _meshData;
    private BoundingBox _bounds;
    private int _activeRequestCount;
    private bool _keepReadable;

    /// <summary>
    /// The mesh geometry data.
    /// </summary>
    public PhosMesh MeshData => _meshData;

    /// <summary>
    /// Bounding box of the mesh.
    /// </summary>
    public BoundingBox Bounds => _bounds;

    /// <summary>
    /// Number of vertices in the mesh.
    /// </summary>
    public int VertexCount => _meshData?.VertexCount ?? 0;

    /// <summary>
    /// Number of triangles in the mesh.
    /// </summary>
    public int TriangleCount
    {
        get
        {
            if (_meshData == null || _meshData.Submeshes.Count == 0) return 0;
            int total = 0;
            foreach (var submesh in _meshData.Submeshes)
            {
                total += submesh.IndexCount / 3;
            }
            return total;
        }
    }

    /// <summary>
    /// Whether to keep mesh data readable after upload to GPU.
    /// </summary>
    public bool KeepReadable
    {
        get => _keepReadable;
        set => _keepReadable = value;
    }

    public override int ActiveRequestCount => _activeRequestCount;

    /// <summary>
    /// Set the mesh data.
    /// </summary>
    /// <param name="mesh">The PhosMesh containing geometry data</param>
    public void SetMeshData(PhosMesh mesh)
    {
        _meshData = mesh;
        _bounds = mesh?.CalculateBoundingBox() ?? new BoundingBox();
        Version++;

        // Upload to hook if available
        if (Hook != null && mesh != null)
        {
            Hook.UploadMesh(mesh);
        }
    }

    /// <summary>
    /// Set a custom bounding box override.
    /// </summary>
    public void SetBounds(BoundingBox bounds)
    {
        _bounds = bounds;
    }

    /// <summary>
    /// Recalculate bounding box from mesh data.
    /// </summary>
    public void RecalculateBounds()
    {
        if (_meshData != null)
        {
            _bounds = _meshData.CalculateBoundingBox();
        }
    }

    /// <summary>
    /// Create a MeshDataAsset from a PhosMesh.
    /// </summary>
    public static MeshDataAsset FromPhosMesh(PhosMesh mesh)
    {
        var asset = new MeshDataAsset();
        asset.InitializeDynamic();
        asset.SetMeshData(mesh);
        return asset;
    }

    /// <summary>
    /// Add an active request for this mesh.
    /// </summary>
    public void AddRequest()
    {
        _activeRequestCount++;
    }

    /// <summary>
    /// Remove an active request for this mesh.
    /// </summary>
    public void RemoveRequest()
    {
        _activeRequestCount = System.Math.Max(0, _activeRequestCount - 1);
    }

    public override void Unload()
    {
        if (!_keepReadable)
        {
            _meshData?.Clear();
        }
        _meshData = null;
        _bounds = new BoundingBox();
        _activeRequestCount = 0;
        base.Unload();
    }
}
