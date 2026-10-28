// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using Lumora.Core;
using Lumora.Source.Godot.Bootstrap;

namespace Lumora.Source.Godot.Rendering;

public static class ViewportQuality
{
    // These multiply the runtime's recommended eye size, which already includes lens correction
    // and the user's runtime resolution setting. 2x silently quadrupled pixel count on desktop.
    private const float DesktopXrRenderTargetMultiplier = 1.2f;
    // Keep the standalone eye-buffer density measured on-device; do not stack viewport scaling.
    private const float StandaloneXrRenderTargetMultiplier = 1.3f;
    // 1.5x here meant 2.25x the pixels of what the runtime already asked for, under 8x MSAA and an FXAA
    // pass on top; a 3080 Ti held 40-50 fps in a near-empty home. The runtime's recommended size is the
    // panel already; desktop VR titles run it at 1.0-1.2x with 4x MSAA. -xlinka
    private const float DesktopXrSupersampleScale = 1.0f;
    // No supersampling on standalone now that the swapchain itself is bigger (see the multiplier
    // above): rendering 1.2x into a small buffer was the stand-in for that, and doing both costs
    // double for a downsample the compositor undoes. -xlinka
    private const float StandaloneXrSupersampleScale = 1.0f;

    // Frame's runtime provides its recommended eye resolution; the Pico-specific 1.3 correction
    // has not been measured there and would add 69% more pixels on a battery-powered GPU.
    internal static float DefaultRenderTargetMultiplier => HeadsetDeviceMode.IsSteamFrame ? 1.0f
        : AndroidDeviceMode.IsHeadset ? StandaloneXrRenderTargetMultiplier : DesktopXrRenderTargetMultiplier;

    // Returns true when the running session was taken down to change the multiplier; the caller has
    // to wait for it to actually be gone before calling Initialize() again.
    public static bool ConfigureOpenXRBeforeInitialize(OpenXRInterface? xrInterface, Action<string>? log = null)
    {
        if (xrInterface == null)
            return false;

        var multiplier = DefaultRenderTargetMultiplier;

        if (xrInterface.IsInitialized())
        {
            // On Android the session is created before any script runs (xr/openxr/enabled starts it
            // at boot). Do NOT try to restart it to get the multiplier in: Uninitialize() marks the
            // interface down, the XR server stops pumping the OpenXR state machine, and the old session
            // sits at Focused forever while the new one fails on "session != nullptr". The multiplier is
            // applied to the running session in ConfigureOpenXRAfterInitialize instead. -xlinka
            return false;
        }

        xrInterface.RenderTargetSizeMultiplier = multiplier;
        log?.Invoke($"OpenXR quality: render target multiplier set to {multiplier:0.##} before initialization.");
        return false;
    }

    public static void ConfigureOpenXRAfterInitialize(OpenXRInterface? xrInterface, Viewport? viewport, Action<string>? log = null)
    {
        if (xrInterface == null || !xrInterface.IsInitialized())
        {
            log?.Invoke("OpenXR quality: skipped because OpenXR is not initialized.");
            return;
        }

        ApplyXrViewportDefaults(viewport);

        // The eye swapchain itself, on a RUNNING session. The class reference still says the
        // multiplier must be set before initialize; 4.7-dev4 recreates the swapchain when it grows,
        // and on the Pico 4 that took the eye buffer from 1504x1504 (0.7x of the 2160 panel, which
        // the compositor then upscaled: the "everything is soft" complaint) to 1955x1955 live.
        // Measured GPU at 72 Hz, MSAA 2x, XR on the root viewport: 1.25x 12.7 ms, 1.3x 13.3 ms,
        // 1.4x 14-18 ms (drops to 59-70). 1.3x holds 72 in the home; a heavier world will want a
        // dynamic step down, which is the next thing to build. -xlinka
        if (HeadsetDeviceMode.IsHeadset)
        {
            float mult = DefaultRenderTargetMultiplier;
            if (System.Math.Abs(xrInterface.RenderTargetSizeMultiplier - mult) > 0.001f)
            {
                var before = xrInterface.GetRenderTargetSize();
                xrInterface.RenderTargetSizeMultiplier = mult;
                var after = xrInterface.GetRenderTargetSize();
                log?.Invoke($"OpenXR quality: render target multiplier {mult:0.##} applied; eye buffer {before.X:0}x{before.Y:0} -> {after.X:0}x{after.Y:0}");
            }
        }

        if (xrInterface == null)
            return;

        try
        {
            // No foveation. On the Pico 4 level 1 measured no GPU saving at all and level 2 reads as a
            // smeared edge; a clean periphery was the complaint. VRS strength is left alone; Godot
            // refuses anything under 0.1 and VRS is only live when a viewport asks for it, so writing
            // zero here only produced a warning on every boot. -xlinka
            xrInterface.FoveationDynamic = false;
            xrInterface.FoveationLevel = 0;
            xrInterface.FoveationWithSubsampledImages = false;
            xrInterface.VrsMinRadius = 1.0f;
        }
        catch (Exception ex)
        {
            log?.Invoke($"OpenXR quality: foveation/VRS override skipped ({ex.Message}).");
        }

        if (viewport is not SubViewport subViewport)
            return;

        try
        {
            var targetSize = xrInterface.GetRenderTargetSize();
            var width = Math.Max(1, (int)Math.Ceiling(targetSize.X));
            var height = Math.Max(1, (int)Math.Ceiling(targetSize.Y));
            var size = new Vector2I(width, height);

            if (subViewport.Size != size)
            {
                subViewport.Size = size;
                log?.Invoke($"OpenXR quality: XR viewport resized to {width}x{height}.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"OpenXR quality: render target size query skipped ({ex.Message}).");
        }
    }

    public static void ApplyXrViewportDefaults(Viewport? viewport)
    {
        if (viewport == null)
            return;

        try
        {
            bool standalone = HeadsetDeviceMode.IsHeadset;
            viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
            viewport.Scaling3DScale = standalone ? StandaloneXrSupersampleScale : DesktopXrSupersampleScale;
            viewport.FsrSharpness = 1.0f;
            // Measured on the Pico 4 at 72 Hz (GPU ms, same scene):
            //   1.0x MSAA4 fov1     6.6-9.1     1.1x MSAA4 fov1    11.0-13.4  (drops to 55-61 fps)
            //   1.2x MSAA2 fov1     6.3-7.6     1.2x MSAA2 fov0     6.5-8.7
            //   1.35x MSAA2 fov1    9.0-11.2    1.2x MSAA4 fov1    12-16      (45 fps)
            // Once the buffer is scaled the mobile renderer leaves its single-pass path and 4x MSAA
            // stops being free; 2x MSAA under 1.2x supersampling is sharper AND cheaper than 4x at 1x.
            // FXAA is a full-resolution pass and adds nothing on top of that. Debanding is a dither
            // folded into the tonemap pass, and a gradient sky in a headset bands without it. A PC headset
            // takes 2x as well: with a toon-shaded avatar up close, 4x was a sixth of the frame. -xlinka
            viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
            viewport.Msaa3D = Viewport.Msaa.Msaa2X;
            viewport.UseTaa = false;
            viewport.UseDebanding = true;
            viewport.TextureMipmapBias = 0f;
            viewport.AnisotropicFilteringLevel = standalone
                ? Viewport.AnisotropicFiltering.Anisotropy4X
                : Viewport.AnisotropicFiltering.Anisotropy16X;
            // Same source of truth as the desktop window: the headset viewport is a different one and
            // would otherwise sit at the project default no matter what the user picked.
            viewport.MeshLodThreshold = EngineSettings.MeshLodThreshold;
            viewport.VrsMode = Viewport.VrsModeEnum.Disabled;
            viewport.VrsUpdateMode = Viewport.VrsUpdateModeEnum.Disabled;

            if (standalone)
            {
                // Shadow atlases sized for a mobile tiler. The project default is a 4096 directional
                // atlas with soft filtering, which on desktop is the single biggest frame cost already
                // (see the render culling notes) and on an Adreno is prohibitive. Hard filtering plus
                // a 2048 atlas keeps the sun shadow readable without a second full-resolution pass.
                RenderingServer.DirectionalShadowAtlasSetSize(2048, true);
                RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.Hard);
                RenderingServer.PositionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.Hard);
                viewport.PositionalShadowAtlasSize = 1024;
                Lumora.Core.Logging.Logger.Debug("OpenXR quality: standalone shadows set to hard, directional atlas 2048, positional atlas 1024.");

            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"OpenXR quality: viewport defaults skipped ({ex.Message}).");
        }
    }

    public static void ApplyRenderScale(Viewport viewport, float scale)
    {
        if (viewport == null)
            return;

        viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = scale;
    }

    public static void ApplyDesktopMirrorDefaults(Viewport viewport)
    {
        // Render at the window's actual size. Post AA blurs small world-space UI lettering;
        // multisampling smooths geometry while keeping the MSDF text shader's edge coverage.
        viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = 1f;
        viewport.Msaa3D = Viewport.Msaa.Msaa2X;
        viewport.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled;
        viewport.UseTaa = false;
    }

    public static void ApplyAntiAliasing(Viewport viewport, int mode)
    {
        if (viewport == null)
            return;

        mode = EngineSettings.SnapAntiAliasing(mode);
        // Clear the previous algorithm as well as enabling the selected one.
        viewport.UseTaa = false;
        viewport.ScreenSpaceAA = mode == 1 ? Viewport.ScreenSpaceAAEnum.Fxaa : Viewport.ScreenSpaceAAEnum.Disabled;
        viewport.Msaa3D = mode switch
        {
            2 => Viewport.Msaa.Msaa2X,
            4 => Viewport.Msaa.Msaa4X,
            8 => Viewport.Msaa.Msaa8X,
            _ => Viewport.Msaa.Disabled,
        };
    }
}
