// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

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
    public readonly Sync<ProjectionType> Projection = new();

    /// <summary>
    /// Field of view in degrees (for Perspective mode)
    /// </summary>
    public readonly Sync<float> FieldOfView = new();

    /// <summary>
    /// Orthographic size (height in world units, for Orthographic mode)
    /// </summary>
    public readonly Sync<float> OrthographicSize = new();

    /// <summary>
    /// Near clipping plane distance
    /// </summary>
    public readonly Sync<float> NearClip = new();

    /// <summary>
    /// Far clipping plane distance
    /// </summary>
    public readonly Sync<float> FarClip = new();

    /// <summary>
    /// Clear mode (Skybox, Color, DepthOnly, Nothing)
    /// </summary>
    public readonly Sync<ClearMode> Clear = new();

    /// <summary>
    /// Background clear color (when Clear = Color)
    /// </summary>
    public readonly Sync<color> BackgroundColor = new();

    /// <summary>
    /// Render target texture (TODO: Replace with platform-agnostic texture type)
    /// </summary>
    public readonly Sync<object> TargetTexture = new();

    /// <summary>
    /// Camera depth (rendering order)
    /// Lower depth cameras render first
    /// </summary>
    public readonly Sync<int> Depth = new();

    /// <summary>
    /// Culling mask (which layers to render)
    /// </summary>
    public readonly Sync<int> CullingMask = new();

    /// <summary>
    /// Render shadows
    /// </summary>
    public readonly Sync<bool> RenderShadows = new();

    /// <summary>
    /// Use occlusion culling
    /// </summary>
    public readonly Sync<bool> UseOcclusionCulling = new();

    /// <summary>
    /// Allow HDR rendering
    /// </summary>
    public readonly Sync<bool> AllowHDR = new();

    /// <summary>
    /// Allow MSAA (multi-sample anti-aliasing)
    /// </summary>
    public readonly Sync<bool> AllowMSAA = new();

    /// <summary>
    /// Viewport rect (normalized 0-1) - TODO: Create platform-agnostic Rect2 type
    /// </summary>
    public readonly Sync<float4> ViewportRect = new();

    /// <summary>
    /// Selective rendering - only render specific objects
    /// </summary>
    public readonly Sync<bool> SelectiveRender = new();

    /// <summary>
    /// Render post-processing effects
    /// </summary>
    public readonly Sync<bool> RenderPostProcessing = new();

    // TODO: Post-processing effects list - needs proper implementation
    // public SyncList<PostProcessingEffect> PostProcessingEffects { get; private set; }

    public override void OnInit()
    {
        base.OnInit();

        // Projection = ProjectionType.Perspective (enum 0, C# default, skip)
        FieldOfView.Value = 60f;
        OrthographicSize.Value = 5f;
        NearClip.Value = 0.05f;
        FarClip.Value = 1000f;
        // Clear = ClearMode.Skybox (enum 0, C# default, skip)
        BackgroundColor.Value = new color(0.2f, 0.2f, 0.2f, 1f);
        // TargetTexture = default (C# default null, skip)
        // Depth = 0 (C# default, skip)
        CullingMask.Value = -1; // All layers
        RenderShadows.Value = true;
        UseOcclusionCulling.Value = true;
        AllowHDR.Value = true;
        AllowMSAA.Value = true;
        ViewportRect.Value = new float4(0f, 0f, 1f, 1f);
        // SelectiveRender = false (C# default, skip)
        RenderPostProcessing.Value = true;
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
