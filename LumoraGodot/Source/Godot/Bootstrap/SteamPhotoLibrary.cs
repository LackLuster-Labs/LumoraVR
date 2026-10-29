// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
#if GODOT_PC
using System;
using System.Collections.Generic;
using Godot;
using Lumora.Core.Assets;
using Lumora.Core.Photos;
using Steamworks;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Source.Godot.Bootstrap;

public static class SteamPhotoLibrary
{
    private static Callback<ScreenshotReady_t>? _ready;
    private static Callback<ScreenshotRequested_t>? _requested;
    private static readonly HashSet<ScreenshotHandle> Pending = new();

    // Steam's own screenshot key is F12 out of the box, the photo key too. Left to itself Steam grabs the window
    // on that press while the photo goes into its library as well: two shots for one press. Hooked, the key asks
    // for a photo instead, and a press the photo keys already answer makes no second one. -xlinka
    public static void Initialize()
    {
        if (_ready != null)
            return;
        _ready = Callback<ScreenshotReady_t>.Create(OnReady);
        _requested = Callback<ScreenshotRequested_t>.Create(OnRequested);
        SteamScreenshots.HookScreenshots(true);
        PhotoLibrary.PhotoTaken += OnPhoto;
    }

    public static void Shutdown()
    {
        PhotoLibrary.PhotoTaken -= OnPhoto;
        if (_requested != null)
            SteamScreenshots.HookScreenshots(false);
        _ready?.Dispose();
        _ready = null;
        _requested?.Dispose();
        _requested = null;
        Pending.Clear();
    }

    private static void OnRequested(ScreenshotRequested_t request)
        => Lumora.Core.Components.HandViewfinder.AskForPhoto();

    // Photo completion runs off-thread; keep Steam calls and callback bookkeeping on the game thread.
    private static void OnPhoto(byte[] bytes, ImageEncoding encoding)
        => Callable.From(() => Register(bytes, encoding)).CallDeferred();

    private static void Register(byte[] bytes, ImageEncoding encoding)
    {
        if (!SteamManager.Initialized || _ready == null)
            return;
        try
        {
            var (rgb, width, height) = DecodeRgb(bytes, encoding);
            // WriteScreenshot accepts RGB, including photos encoded as WebP (which the file API rejects).
            var handle = SteamScreenshots.WriteScreenshot(rgb, (uint)rgb.Length, width, height);
            if (handle == ScreenshotHandle.Invalid)
                LumoraLogger.Warn("Photos: Steam rejected the screenshot");
            else
                Pending.Add(handle);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Photos: could not add Steam screenshot: {ex.Message}");
        }
    }

    internal static (byte[] Rgb, int Width, int Height) DecodeRgb(byte[] bytes, ImageEncoding encoding)
    {
        using var image = new Image();
        var error = encoding switch
        {
            ImageEncoding.Png => image.LoadPngFromBuffer(bytes),
            ImageEncoding.Jpg => image.LoadJpgFromBuffer(bytes),
            _ => image.LoadWebpFromBuffer(bytes),
        };
        if (error != Error.Ok || image.IsEmpty())
            throw new InvalidOperationException($"Could not decode photo ({error})");
        image.Convert(Image.Format.Rgb8);
        return (image.GetData(), image.GetWidth(), image.GetHeight());
    }

    private static void OnReady(ScreenshotReady_t result)
    {
        if (!Pending.Remove(result.m_hLocal))
            return;
        if (result.m_eResult == EResult.k_EResultOK)
            LumoraLogger.Log("Photos: added to Steam screenshots");
        else
            LumoraLogger.Warn($"Photos: Steam screenshot save failed: {result.m_eResult}");
    }
}
#endif
