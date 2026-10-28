// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.IO;
using Godot;

namespace Lumora.Source.Godot.Bootstrap;

// This describes the machine running Lumora, not a headset streaming from it. A Frame controller
// profile (or SteamVR runtime name) alone says nothing about where rendering takes place.
public static class HeadsetDeviceMode
{
    private static bool? _steamFrame;
    public static bool IsSteamFrame => _steamFrame ??= DetectSteamFrame();
    public static bool IsHeadset => AndroidDeviceMode.IsHeadset || IsSteamFrame;

    private static bool DetectSteamFrame()
    {
        if (!OS.HasFeature("linux")) return false;
        if (OS.HasFeature("steam_frame") || LaunchArgs.HasFlag("--steam-frame")) return true;
        if (IsFrameModel(OS.GetModelName())) return true;
        // Linux may report a generic model through Godot. These identify local hardware even when
        // an x86_64 build is running under FEX; process architecture is not a device identifier.
        foreach (var path in new[] { "/sys/firmware/devicetree/base/model", "/proc/device-tree/model", "/sys/class/dmi/id/product_name" })
        {
            try
            {
                using var reader = File.OpenText(path);
                char[] buffer = new char[256];
                if (IsFrameModel(new string(buffer, 0, reader.Read(buffer, 0, buffer.Length)))) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        // The board files above can be hidden inside Steam's runtime container, and Steam itself calls the Frame a
        // Deck. Seen on a Frame 10-30: model GenericDevice, no Frame string in any of them, renderer "Turnip Adreno
        // (TM) 750". A Deck is AMD, and the Frame is the only Steam machine that runs this Linux build on a
        // Qualcomm GPU (through FEX), so the GPU answers when the board will not. -xlinka
        return IsFrameGpu(RenderingServer.GetVideoAdapterVendor()) || IsFrameGpu(RenderingServer.GetVideoAdapterName());
    }

    private static bool IsFrameGpu(string? gpu)
        => gpu?.Contains("Adreno", StringComparison.OrdinalIgnoreCase) == true
            || gpu?.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsFrameModel(string? model)
        => model?.Contains("Steam Frame", StringComparison.OrdinalIgnoreCase) == true
            || model?.Contains("SteamFrame", StringComparison.OrdinalIgnoreCase) == true;
}
