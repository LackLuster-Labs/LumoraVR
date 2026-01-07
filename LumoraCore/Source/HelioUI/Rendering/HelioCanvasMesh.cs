using Lumora.Core.Math;
using Lumora.Core.Phos;
using Lumora.Core.Components.Meshes;

namespace Lumora.Core.HelioUI.Rendering;

/// <summary>
/// Procedural mesh for rendering a HelioCanvas in 3D space.
/// Generates a quad mesh sized to the canvas reference size.
/// </summary>
public class HelioCanvasMesh : ProceduralMesh
{
    // ===== Sync Fields =====

    /// <summary>Canvas reference size in UI units</summary>
    public readonly Sync<float2> CanvasSize;

    /// <summary>Pixel scale (UI units per world unit)</summary>
    public readonly Sync<float> PixelScale;

    /// <summary>Background color of the canvas</summary>
    public readonly Sync<color> BackgroundColor;

    // ===== Private State =====

    private PhosQuad _quad;
    private float2 _canvasSize;
    private float _pixelScale;
    private color _backgroundColor;

    // ===== Constructor =====

    public HelioCanvasMesh()
    {
        CanvasSize = new Sync<float2>(this, new float2(400f, 600f));
        PixelScale = new Sync<float>(this, 100f);
        BackgroundColor = new Sync<color>(this, new color(0.1f, 0.1f, 0.12f, 0.95f));
    }

    // ===== Lifecycle =====

    public override void OnAwake()
    {
        base.OnAwake();
        // NOTE: Do NOT use SubscribeToChanges here!
        // The Canvas manages when to regenerate mesh via RequestVisualRebuild.
        // Auto-subscribing would cause double regeneration and kill performance.
    }

    // ===== Mesh Generation =====

    protected override void PrepareAssetUpdateData()
    {
        _canvasSize = CanvasSize.Value;
        _pixelScale = PixelScale.Value;
        _backgroundColor = BackgroundColor.Value;
    }

    protected override void UpdateMeshData(PhosMesh mesh)
    {
        bool geometryChanged = false;

        if (_quad == null)
        {
            var submesh = new PhosTriangleSubmesh(mesh);
            mesh.Submeshes.Add(submesh);
            _quad = new PhosQuad(submesh);
            _quad.UseColors = false; // UI texture supplies color; keep mesh neutral
            geometryChanged = true;
        }

        // Calculate world size from canvas size and pixel scale
        float scale = _pixelScale > 0 ? _pixelScale : 100f;
        float2 worldSize = _canvasSize / scale;

        _quad.Size = worldSize;
        _quad.Rotation = floatQ.Identity;
        _quad.UVScale = float2.One;
        _quad.UVOffset = float2.Zero;

        // Mesh colors stay neutral; background comes from viewport texture
        _quad.Color = null;

        _quad.Update();

        uploadHint[MeshUploadHint.Flag.Geometry] = geometryChanged;
    }

    protected override void ClearMeshData()
    {
        _quad = null;
    }

    /// <summary>
    /// Get the world-space size of the canvas mesh.
    /// </summary>
    public float2 GetWorldSize()
    {
        float scale = PixelScale.Value > 0 ? PixelScale.Value : 100f;
        return CanvasSize.Value / scale;
    }
}
