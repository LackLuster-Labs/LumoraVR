using System;
using Godot;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;
using LumoraEngine = Lumora.Core.Engine;

namespace Aquamarine.Source.Godot.Services;

/// <summary>
/// Service that captures periodic thumbnails of hosted sessions.
/// </summary>
public partial class SessionThumbnailService : Node
{
    /// <summary>
    /// Thumbnail capture interval in seconds.
    /// </summary>
    [Export] public float CaptureInterval { get; set; } = 30f;

    /// <summary>
    /// Thumbnail width in pixels.
    /// </summary>
    [Export] public int ThumbnailWidth { get; set; } = 256;

    /// <summary>
    /// Thumbnail height in pixels.
    /// </summary>
    [Export] public int ThumbnailHeight { get; set; } = 144;

    /// <summary>
    /// JPEG quality (0-100). Lower = smaller file, worse quality.
    /// </summary>
    [Export] public float JpegQuality { get; set; } = 75f;

    private float _captureTimer;
    private bool _isCapturing;

    public override void _Ready()
    {
        AquaLogger.Log("SessionThumbnailService: Initialized");
    }

    public override void _Process(double delta)
    {
        _captureTimer += (float)delta;

        if (_captureTimer >= CaptureInterval)
        {
            _captureTimer = 0;
            CaptureAndUpdateThumbnail();
        }
    }

    /// <summary>
    /// Capture a thumbnail and update the current session's metadata.
    /// </summary>
    public void CaptureAndUpdateThumbnail()
    {
        if (_isCapturing)
            return;

        var world = LumoraEngine.Current?.WorldManager?.FocusedWorld;
        if (world?.Session == null)
            return;

        // Only capture for sessions we're hosting (authority)
        if (!world.IsAuthority)
            return;

        _isCapturing = true;

        try
        {
            var base64 = CaptureViewportToBase64();
            if (!string.IsNullOrEmpty(base64))
            {
                world.Session.UpdateMetadata(meta =>
                {
                    meta.ThumbnailBase64 = base64;
                });
                AquaLogger.Log("SessionThumbnailService: Thumbnail updated");
            }
        }
        catch (Exception ex)
        {
            AquaLogger.Warn($"SessionThumbnailService: Capture failed - {ex.Message}");
        }
        finally
        {
            _isCapturing = false;
        }
    }

    /// <summary>
    /// Capture the main viewport and convert to base64 JPEG.
    /// </summary>
    private string CaptureViewportToBase64()
    {
        // Get the main viewport
        var viewport = GetViewport();
        if (viewport == null)
            return null;

        // Get the viewport texture
        var viewportTexture = viewport.GetTexture();
        if (viewportTexture == null)
            return null;

        // Get image from texture
        var image = viewportTexture.GetImage();
        if (image == null)
            return null;

        // Resize to thumbnail size
        image.Resize(ThumbnailWidth, ThumbnailHeight, Image.Interpolation.Bilinear);

        // Convert to JPEG bytes (smaller than PNG)
        var jpegData = image.SaveJpgToBuffer(JpegQuality / 100f);
        if (jpegData == null || jpegData.Length == 0)
            return null;

        // Convert to base64
        return Convert.ToBase64String(jpegData);
    }

    /// <summary>
    /// Force an immediate thumbnail capture.
    /// </summary>
    public void CaptureNow()
    {
        _captureTimer = CaptureInterval; // Will trigger on next frame
    }
}
