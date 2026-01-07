using Lumora.Core.Math;
using Lumora.Core.Phos;

namespace Lumora.Core.Components.Meshes;

/// <summary>
/// Base class for procedural mesh components.
/// Generates mesh geometry at runtime based on component properties.
/// </summary>
public abstract class ProceduralMesh : ImplementableComponent
{
    protected PhosMesh? phosMesh { get; private set; }
    protected MeshUploadHint uploadHint;

    private bool _isDirty = false;

    // ===== Public Properties (for hook access) =====

    /// <summary>Get the PhosMesh data (for hook to upload)</summary>
    public PhosMesh? PhosMesh => phosMesh;

    /// <summary>Get the upload hint (which channels changed)</summary>
    public MeshUploadHint UploadHint => uploadHint;

    /// <summary>Check if mesh needs to be uploaded</summary>
    public bool IsDirty => _isDirty;

    // ===== Sync Fields =====

    /// <summary>Override the bounding box with a custom value</summary>
    public readonly Sync<bool> OverrideBoundingBox;

    /// <summary>Custom bounding box (if OverrideBoundingBox is true)</summary>
    public readonly Sync<BoundingBox> OverridenBoundingBox;

    // ===== Constructor =====

    protected ProceduralMesh()
    {
        OverrideBoundingBox = new Sync<bool>(this, false);
        OverridenBoundingBox = new Sync<BoundingBox>(this, new BoundingBox());
    }

    // ===== Lifecycle Hooks =====

    public override void OnAwake()
    {
        base.OnAwake();
    }

    public override void OnStart()
    {
        base.OnStart();
        // Generate initial mesh
        RegenerateMesh();
    }

    // ===== Abstract Methods =====

    /// <summary>
    /// Prepare data for mesh update.
    /// Copy sync field values to local variables for thread safety.
    /// </summary>
    protected abstract void PrepareAssetUpdateData();

    /// <summary>
    /// Update the PhosMesh data based on component properties.
    /// Called when mesh needs to be regenerated.
    /// </summary>
    protected abstract void UpdateMeshData(PhosMesh mesh);

    /// <summary>
    /// Clear mesh data (called when component is disabled/destroyed).
    /// </summary>
    protected abstract void ClearMeshData();

    // ===== Mesh Generation =====

    /// <summary>
    /// Prepare for mesh update.
    /// Creates PhosMesh if needed and sets all upload hint flags.
    /// </summary>
    private void PrepareMeshUpdate()
    {
        if (phosMesh == null)
        {
            phosMesh = new PhosMesh();
        }
        uploadHint.SetAll();
    }

    /// <summary>
    /// Regenerate the mesh.
    /// Call this when properties change.
    /// </summary>
    public void RegenerateMesh()
    {
        PrepareMeshUpdate();
        PrepareAssetUpdateData();
        UpdateMeshData(phosMesh!);
        MarkDirty();
    }

    /// <summary>
    /// Mark mesh as dirty (needs upload to GPU).
    /// Triggers hook update to apply changes to Godot.
    /// </summary>
    protected void MarkDirty()
    {
        _isDirty = true;
        RunApplyChanges();
    }

    /// <summary>
    /// Clear dirty flag.
    /// </summary>
    public void ClearDirty()
    {
        _isDirty = false;
    }

    /// <summary>
    /// Get the PhosMesh data.
    /// </summary>
    public PhosMesh? GetPhosMesh()
    {
        return phosMesh;
    }

    /// <summary>
    /// Get the upload hint (which channels changed).
    /// </summary>
    public MeshUploadHint GetUploadHint()
    {
        return uploadHint;
    }

    /// <summary>
    /// Get the bounding box for this mesh.
    /// </summary>
    public BoundingBox GetBoundingBox()
    {
        if (OverrideBoundingBox.Value)
        {
            return OverridenBoundingBox.Value;
        }

        if (phosMesh != null)
        {
            return phosMesh.CalculateBoundingBox();
        }

        return new BoundingBox();
    }

    // ===== Cleanup =====

    public override void OnDestroy()
    {
        phosMesh?.Clear();
        phosMesh = null;
        ClearMeshData();
        base.OnDestroy();
    }

    // ===== Helper: Subscribe to Property Changes =====

	/// <summary>
	/// Subscribe a Sync field to trigger mesh regeneration on change.
	/// </summary>
	protected void SubscribeToChanges<T>(SyncField<T> sync)
	{
		sync.OnChanged += (newVal) => RegenerateMesh();
	}
}
