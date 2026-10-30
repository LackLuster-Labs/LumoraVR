// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// A host command every GPU machine applies once. Packed into ints, so only append.
public enum FluidCommandKind
{
    Emit = 0,
    Burst = 1,
    Remove = 2,
    ClearAll = 3,
    ClearOrigin = 4,
    Evaporate = 5,
    Settle = 6,
    Epoch = 7,
    FadeOrigin = 8,
}
