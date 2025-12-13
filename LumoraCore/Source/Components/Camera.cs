using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// Renders the scene from a viewpoint.
/// </summary>
[ComponentCategory("Rendering")]
public class Camera : ImplementableComponent
{
    /// <summary>
    /// Camera projection mode (Perspective or Orthographic)
    /// </summary>
    public Sync<ProjectionType> Projection { get; private set; }

    /// <summary>
    /// Field of view in degrees (for Perspective mode)
    /// </summary>
    public Sync<float> FieldOfView { get; private set; }

    /// <summary>
    /// Orthographic size (height in world units, for Orthographic mode)
    /// </summary>
    public Sync<float> OrthographicSize { get; private set; }

    /// <summary>
    /// Near clipping plane distance
    /// </summary>
    public Sync<float> NearClip { get; private set; }

    /// <summary>
    /// Far clipping plane distance
    /// </summary>
    public Sync<float> FarClip { get; private set; }

    /// <summary>
    /// Clear mode (Skybox, Color, DepthOnly, Nothing)
    /// </summary>
    public Sync<ClearMode> Clear { get; private set; }

    /// <summary>
    /// Background clear color (when Clear = Color)
    /// </summary>
    public Sync<color> BackgroundColor { get; private set; }

    /// <summary>
    /// Render target texture (TODO: Replace with platform-agnostic texture type)
    /// </summary>
    public Sync<object> TargetTexture { get; private set; }

    /// <summary>
    /// Camera depth (rendering order)
    /// Lower depth cameras render first
    /// </summary>
    public Sync<int> Depth { get; private set; }

    /// <summary>
    /// Culling mask (which layers to render)
    /// </summary>
    public Sync<int> CullingMask { get; private set; }

    /// <summary>
    /// Render shadows
    /// </summary>
    public Sync<bool> RenderShadows { get; private set; }

    /// <summary>
    /// Use occlusion culling
    /// </summary>
    public Sync<bool> UseOcclusionCulling { get; private set; }

    /// <summary>
    /// Allow HDR rendering
    /// </summary>
    public Sync<bool> AllowHDR { get; private set; }

    /// <summary>
    /// Allow MSAA (multi-sample anti-aliasing)
    /// </summary>
    public Sync<bool> AllowMSAA { get; private set; }

    /// <summary>
    /// Viewport rect (normalized 0-1) - TODO: Create platform-agnostic Rect2 type
    /// </summary>
    public Sync<float4> ViewportRect { get; private set; }

    /// <summary>
    /// Selective rendering - only render specific objects
    /// </summary>
    public Sync<bool> SelectiveRender { get; private set; }

    /// <summary>
    /// Render post-processing effects
    /// </summary>
    public Sync<bool> RenderPostProcessing { get; private set; }

    // TODO: Post-processing effects list - needs proper implementation
    // public SyncList<PostProcessingEffect> PostProcessingEffects { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        // Initialize sync members
        Projection = new Sync<ProjectionType>(this, ProjectionType.Perspective);
        FieldOfView = new Sync<float>(this, 60f);
        OrthographicSize = new Sync<float>(this, 5f);
        NearClip = new Sync<float>(this, 0.05f);
        FarClip = new Sync<float>(this, 1000f);
        Clear = new Sync<ClearMode>(this, ClearMode.Skybox);
        BackgroundColor = new Sync<color>(this, new color(0.2f, 0.2f, 0.2f, 1f));
        TargetTexture = new Sync<object>(this, default);
        Depth = new Sync<int>(this, 0);
        CullingMask = new Sync<int>(this, -1); // All layers
        RenderShadows = new Sync<bool>(this, true);
        UseOcclusionCulling = new Sync<bool>(this, true);
        AllowHDR = new Sync<bool>(this, true);
        AllowMSAA = new Sync<bool>(this, true);
        ViewportRect = new Sync<float4>(this, new float4(0f, 0f, 1f, 1f));
        SelectiveRender = new Sync<bool>(this, false);
        RenderPostProcessing = new Sync<bool>(this, true);
        // PostProcessingEffects = new SyncList<PostProcessingEffect>();
    }

    public override void OnStart()
    {
        base.OnStart();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
    }

    /// <summary>
    /// Get the aspect ratio of the camera
    /// </summary>
    public float AspectRatio
    {
        get
        {
            // TODO: Get from render target or screen
            return 16f / 9f;
        }
    }

    /// <summary>
    /// Check if this camera is rendering to a texture
    /// </summary>
    public bool IsRenderTexture
    {
        get { return TargetTexture.Value != null; }
    }
}

/// <summary>
/// Camera projection types.
/// </summary>
public enum ProjectionType
{
    Perspective,   // Perspective projection (realistic 3D)
    Orthographic   // Orthographic projection (no depth perspective)
}

/// <summary>
/// Camera clear modes.
/// </summary>
public enum ClearMode
{
    Skybox,      // Clear to skybox
    Color,       // Clear to solid color
    DepthOnly,   // Clear depth only, keep color
    Nothing      // Don't clear anything
}
