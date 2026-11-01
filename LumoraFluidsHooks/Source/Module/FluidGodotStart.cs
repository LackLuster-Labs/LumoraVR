// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids.Hooks;

// The module's Godot half, run once on the main thread by the first fluid hook to initialize. It says what the
// renderer is, runs the device check (a main RenderingDevice and the limits the kernels need) and, on the GPU path,
// hooks the device into the frame at FramePreDraw. The answer holds for the session and is logged once. A session
// with no fluid in it never gets here and never makes a Godot call from the module. -xlinka
public static class FluidGodotStart
{
    private static bool _started;

    public static bool Started => _started;

    public static void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;

        string method = string.Empty;
        RenderingDevice? device = null;
        try
        {
            method = RenderingServer.GetCurrentRenderingMethod();
            device = RenderingServer.GetRenderingDevice();
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: renderer query failed: {ex.Message}");
        }
        FluidPlatform.Report(method, device != null);

        var gpu = FluidGpuDevice.Instance;
        if (!FluidPlatform.SimulationAllowed)
            gpu.BeginViewOnly(FluidPlatform.StandaloneReason);
        else if (device == null)
            gpu.BeginViewOnly("no RenderingDevice");
        else if (!FluidGpuGate.Check(device, out var tier, out var reason))
            gpu.BeginViewOnly(reason);
        else
        {
            gpu.BeginGpu(device, tier);
            RenderingServer.FramePreDraw += FluidGpuDevice.OnFramePreDraw;
        }
        LumoraLogger.Log($"Fluids: {gpu.Status}");
    }
}
