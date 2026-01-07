using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

/// <summary>
/// Procedural box mesh component.
/// Generates box geometry based on Size and UVScale properties.
/// </summary>
public class BoxMesh : ProceduralMesh
{
    // ===== Sync Fields =====

    /// <summary>Size of the box</summary>
    public readonly Sync<float3> Size;

    /// <summary>UV scale for texture mapping</summary>
    public readonly Sync<float3> UVScale;

    /// <summary>Scale UVs proportionally with size</summary>
    public readonly Sync<bool> ScaleUVWithSize;

    // ===== Private State =====

    private PhosBox? box;
    private float3 _size;
    private float3 _uvScale;
    private bool _scaleUVWithSize;

    // ===== Constructor =====

    public BoxMesh()
    {
        Size = new Sync<float3>(this, float3.One);
        UVScale = new Sync<float3>(this, float3.One);
        ScaleUVWithSize = new Sync<bool>(this, false);
    }

    // ===== Lifecycle =====

    public override void OnAwake()
    {
        base.OnAwake();

        // Subscribe to property changes
        SubscribeToChanges(Size);
        SubscribeToChanges(UVScale);
        SubscribeToChanges(ScaleUVWithSize);
    }

    // ===== Mesh Generation =====

    protected override void PrepareAssetUpdateData()
    {
        // Copy sync values to local variables (thread-safe)
        _size = Size.Value;
        _uvScale = UVScale.Value;
        _scaleUVWithSize = ScaleUVWithSize.Value;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        // Mark geometry as changed if this is the first update
        uploadHint[MeshUploadHint.Flag.Geometry] = box == null;

        if (box == null)
        {
            // Create submesh and box
            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            box = new PhosBox(submesh);
        }

        // Update box properties
        box.Size = _size;

        // Scale UVs with size if requested
        if (_scaleUVWithSize)
        {
            box.UVScale = _uvScale * _size;
        }
        else
        {
            box.UVScale = _uvScale;
        }

        // Regenerate vertex data
        box.Update();
    }

    protected override void ClearMeshData()
    {
        box = null;
    }

    // ===== Utility Methods =====

    /// <summary>
    /// Create a box collider component that matches this box mesh.
    /// </summary>
    public void CreateBoxCollider()
    {
        // TODO: Implement when BoxCollider component exists
        // var collider = Slot.AttachComponent<BoxCollider>();
        // collider.Size.DriveFrom(Size);
    }
}
