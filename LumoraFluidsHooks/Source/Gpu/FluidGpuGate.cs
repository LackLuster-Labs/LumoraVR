// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using LumoraEngine = Lumora.Core.Engine;

namespace Lumora.Fluids.Hooks;

// The device check at first use, run once on the main thread by the Godot half of the boot. A machine that fails any
// part of it is view only for the session: it draws the settled ledger and the spots on people, and its own user is
// coated through the keeper's proposals.
//
// Android builds (Pico, Quest, phones) and the standalone Frame never simulate in v1. A headset in a light world has
// about 0.6 ms of GPU to spare and a fluid frame needs more, and their compute drivers are the least trusted we ship
// to; they take the View rung of the ladder from the start rather than reaching it by failing a budget.
//
// The limits are the ones every kernel needs. The storage-buffer floor is the most the chain binds in one kernel:
// the solver's frame end A declares 23 (predict 22), and the 3080 Ti reports over a million, so no desktop card we
// know of is shut out (Vulkan only promises 4). Every compute shader in the module's
// folder must also have come out of the importer as SPIR-V; one that did not would fail at pipeline creation in the
// middle of a world, which is the wrong place to find out.
//
// The tier is Auto's: the stored calibration when it was taken on this card and driver, else the class guess
// (integrated and the Deck on Low, a discrete card on Medium). What a tier then runs is FluidQuality's one table: the
// awake caps G0m set on the real kernels (Ultra 20k, High 5k, Medium 3k, Low 1k at 2 substeps), which the
// calibration's cost model fits a card against too, so the gate keeps no copy of them. -xlinka
public static class FluidGpuGate
{
    public const int MinStorageBuffers = 23;
    public const int MinSharedMemory = 16 * 1024;
    public const int MinWorkgroupSizeX = 128;
    public const string ComputeFolder = "res://Modules/Fluids/Compute";

    // Filled by Check, for the calibration run and the inspector.
    public static string GpuName { get; private set; } = string.Empty;

    public static string DriverVersion { get; private set; } = string.Empty;

    public static bool Integrated { get; private set; }

    public static int KernelsChecked { get; private set; }

    // True when Auto came from a stored calibration rather than the class guess; false asks for a calibration run.
    public static bool Calibrated { get; private set; }

    public static bool Check(RenderingDevice device, out FluidGpuTier tier, out string reason)
    {
        tier = FluidGpuTier.None;
        reason = string.Empty;

        // Call 1: these platforms draw and never simulate, however capable the device looks.
        if (!FluidPlatform.SimulationAllowed)
        {
            reason = FluidPlatform.StandaloneReason;
            return false;
        }
        if (device == null)
        {
            reason = "no RenderingDevice";
            return false;
        }

        ulong storage = device.LimitGet(RenderingDevice.Limit.MaxStorageBuffersPerShaderStage);
        ulong shared = device.LimitGet(RenderingDevice.Limit.MaxComputeSharedMemorySize);
        ulong sizeX = device.LimitGet(RenderingDevice.Limit.MaxComputeWorkgroupSizeX);
        if (!CheckLimits(storage, shared, sizeX, out reason))
            return false;
        if (!CheckKernels(out reason))
            return false;

        ReadAdapter();
        tier = AutoTier();
        return true;
    }

    // Pure, so a check can fake a device below the gate.
    public static bool CheckLimits(ulong storageBuffers, ulong sharedMemory, ulong workgroupSizeX, out string reason)
    {
        if (storageBuffers < MinStorageBuffers)
            reason = $"{storageBuffers} storage buffers per stage, needs {MinStorageBuffers}";
        else if (sharedMemory < MinSharedMemory)
            reason = $"{sharedMemory} B of shared memory, needs {MinSharedMemory}";
        else if (workgroupSizeX < MinWorkgroupSizeX)
            reason = $"workgroups {workgroupSizeX} wide, needs {MinWorkgroupSizeX}";
        else
            reason = string.Empty;
        return reason.Length == 0;
    }

    // The folder is listed through the resource loader, which reports the source names in an export too, so the
    // gate follows whatever kernels the module ships instead of a list that would go stale.
    private static bool CheckKernels(out string reason)
    {
        reason = string.Empty;
        KernelsChecked = 0;
        string[] entries;
        try
        {
            entries = ResourceLoader.ListDirectory(ComputeFolder);
        }
        catch (Exception ex)
        {
            reason = $"compute folder unreadable: {ex.Message}";
            return false;
        }
        foreach (var entry in entries)
        {
            if (!entry.EndsWith(".glsl", StringComparison.OrdinalIgnoreCase))
                continue;
            string path = ComputeFolder + "/" + entry;
            var file = ResourceLoader.Load<RDShaderFile>(path);
            var spirv = file?.GetSpirV();
            if (spirv == null)
            {
                reason = $"kernel {entry} did not load";
                return false;
            }
            string error = spirv.CompileErrorCompute ?? string.Empty;
            if (error.Length > 0)
            {
                reason = $"kernel {entry} did not compile";
                return false;
            }
            KernelsChecked++;
        }
        if (KernelsChecked == 0)
        {
            reason = "no compute kernels shipped";
            return false;
        }
        return true;
    }

    private static void ReadAdapter()
    {
        try
        {
            GpuName = RenderingServer.GetVideoAdapterName();
            var driver = OS.GetVideoAdapterDriverInfo();
            DriverVersion = driver != null && driver.Length > 1 ? driver[1] : string.Empty;
            Integrated = RenderingServer.GetVideoAdapterType() == RenderingDevice.DeviceType.IntegratedGpu;
        }
        catch (Exception)
        {
            GpuName = string.Empty;
            DriverVersion = string.Empty;
            Integrated = false;
        }
    }

    public static FluidGpuTier AutoTier()
    {
        var calibration = FluidCalibration.LoadFor(GpuName, DriverVersion);
        Calibrated = calibration != null;
        if (calibration != null)
            return calibration.PickTier();
        return LumoraEngine.IsStandaloneHeadset ? FluidGpuTier.Standalone : FluidQuality.InitialTier(Integrated);
    }
}
