// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// The one door from the data side to whatever simulates. The game puts its GPU device behind it, headless
// tests a scripted fake; with nothing behind it (FluidGpu.Current null) the machine is view only. Every member
// is called on the world thread, which under the Safe thread model is also the main and render thread. -xlinka
public interface IFluidGpu
{
    FluidGpuPath Path { get; }

    FluidGpuTier Tier { get; }

    // XR active, or at least 27 frames drawn in the last second. A minimized desktop window stops drawing and its
    // simulation freezes while it still looks focused, so keeper candidacy needs this bit (FluidPerson.Gpu).
    bool DrawingInRealTime { get; }

    // One line for the inspector and the log, e.g. "GPU ready (Forward+, High)" or "view only (Android)".
    string Status { get; }

    // This world's simulation, made on first use. Null while Path is not Gpu.
    IFluidGpuWorld? Open(World world);

    // Frees the world's GPU buffers at once; World.WhenDestroyed calls it.
    void Close(World world);
}
