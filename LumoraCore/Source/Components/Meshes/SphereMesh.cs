using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

/// <summary>
/// Procedural sphere mesh component.
/// Generates UV sphere geometry based on Radius, Segments, and Rings properties.
/// </summary>
[ComponentCategory("Assets/Procedural Meshes")]
public class SphereMesh : ProceduralMesh
{
    // ===== Sync Fields =====

    /// <summary>Radius of the sphere</summary>
    public readonly Sync<float> Radius;

    /// <summary>Number of horizontal segments (longitude divisions)</summary>
    public readonly Sync<int> Segments;

    /// <summary>Number of vertical rings (latitude divisions)</summary>
    public readonly Sync<int> Rings;

    /// <summary>Shading mode (smooth or flat)</summary>
    public readonly Sync<SphereShading> Shading;

    /// <summary>UV scale for texture mapping</summary>
    public readonly Sync<float2> UVScale;

    /// <summary>Render both sides of the sphere</summary>
    public readonly Sync<bool> DualSided;

    // ===== Private State =====

    private PhosSphere? sphere;
    private float _radius;
    private int _segments;
    private int _rings;
    private float2 _uvScale;
    private SphereShading _shading;

    // ===== Constructor =====

    public SphereMesh()
    {
        Radius = new Sync<float>(this, 0.5f);
        Segments = new Sync<int>(this, 32);
        Rings = new Sync<int>(this, 16);
        Shading = new Sync<SphereShading>(this, SphereShading.Smooth);
        UVScale = new Sync<float2>(this, float2.One);
        DualSided = new Sync<bool>(this, false);
    }

    // ===== Lifecycle =====

    public override void OnAwake()
    {
        base.OnAwake();

        // Subscribe to property changes
        SubscribeToChanges(Radius);
        SubscribeToChanges(Segments);
        SubscribeToChanges(Rings);
        SubscribeToChanges(Shading);
        SubscribeToChanges(UVScale);
        SubscribeToChanges(DualSided);
    }

    // ===== Mesh Generation =====

    protected override void PrepareAssetUpdateData()
    {
        // Copy sync values to local variables (thread-safe)
        _radius = Radius.Value;
        _segments = LuminaMath.Clamp(Segments.Value, 3, 256);
        _rings = LuminaMath.Clamp(Rings.Value, 3, 256);
        _uvScale = UVScale.Value;
        _shading = Shading.Value;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        bool geometryChanged = false;

        // Check if we need to regenerate topology
        if (sphere == null || sphere.Segments != _segments || sphere.Rings != _rings || sphere.Shading != _shading)
        {
            if (sphere != null)
            {
                sphere.Remove();
            }

            // Create submesh and sphere
            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            sphere = new PhosSphere(submesh, _segments, _rings, _shading);
            geometryChanged = true;
        }

        // Update sphere properties
        sphere.Radius = _radius;
        sphere.UVScale = _uvScale;

        // Regenerate vertex data
        sphere.Update();

        uploadHint[MeshUploadHint.Flag.Geometry] = geometryChanged;
    }

    protected override void ClearMeshData()
    {
        sphere = null;
    }

    // ===== Utility Methods =====

    /// <summary>
    /// Create a sphere collider component that matches this sphere mesh.
    /// </summary>
    public void CreateSphereCollider()
    {
        // TODO: Implement when SphereCollider component exists
        // var collider = Slot.AttachComponent<SphereCollider>();
        // collider.Radius.DriveFrom(Radius);
    }
}
