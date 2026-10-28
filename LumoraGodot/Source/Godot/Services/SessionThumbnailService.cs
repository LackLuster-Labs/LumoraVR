// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;
using LumoraEngine = Lumora.Core.Engine;

namespace Lumora.Source.Godot.Services;

// Keeps a local preview of every visited world and publishes hosted previews into session metadata.
// Announcements carry the
// bytes inline, which is why it is a small JPEG on a slow timer rather than a real screenshot.
//
// The capture itself goes through InputInterface.TryCaptureWorldView, the same seam the sidecar
// beside a saved world uses, so there is one implementation of "read the viewport and encode it",
// both pictures come out the same size, and neither is a picture of the dash. A build with no view
// leaves the seam null and this quietly does nothing. -xlinka
public partial class SessionThumbnailService : Node
{
    [Export] public float CaptureInterval { get; set; } = 30f;

    [Export] public int ThumbnailWidth { get; set; } = 256;

    [Export] public int ThumbnailHeight { get; set; } = 144;

    private float _captureTimer;
    private Lumora.Core.World? _world;

    public override void _Process(double delta)
    {
        var world = LumoraEngine.Current?.WorldManager?.FocusedWorld;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            // Let the newly focused viewport render before its first capture.
            _captureTimer = CaptureInterval - 1f;
        }
        _captureTimer += (float)delta;
        if (_captureTimer < CaptureInterval)
            return;
        _captureTimer = CaptureAndUpdateThumbnail() ? 0f : CaptureInterval - 1f;
    }

    private bool CaptureAndUpdateThumbnail()
    {
        var world = LumoraEngine.Current?.WorldManager?.FocusedWorld;
        if (world == null || world.IsDestroyed || world.IsLoading)
            return false;

        var input = LumoraEngine.Current?.InputInterface;
        if (input == null || input.IsDashboardOpen || Lumora.Core.Components.WorldLoadIndicator.Showing)
            return false;

        try
        {
            if (!input.TryCaptureWorldView(ThumbnailWidth, ThumbnailHeight, out var jpeg) || jpeg.Length == 0)
                return false;
            var base64 = Convert.ToBase64String(jpeg);
            world.LocalThumbnailBase64 = base64;
            if (world.IsAuthority)
                world.Session?.UpdateMetadata(meta => meta.ThumbnailBase64 = base64);
            return true;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"SessionThumbnailService: capture failed - {ex.Message}");
            return false;
        }
    }
}
