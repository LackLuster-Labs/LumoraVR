// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// What this machine does with fluid, decided once by the device check at the first fluid hook.
public enum FluidGpuPath
{
    // No fluid hook has run yet, so nothing has asked the renderer.
    Pending = 0,
    // Simulates on the GPU.
    Gpu = 1,
    // Never simulates or dispatches: draws the settled ledger as blobs and the spots on people.
    ViewOnly = 2,
}
