// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// A machine's fluid tier, local to it. It changes spacing, detail and looks, never the ledger or gameplay.
public enum FluidGpuTier
{
    None = 0,
    Standalone = 1,
    Low = 2,
    Medium = 3,
    High = 4,
    Ultra = 5,
}
