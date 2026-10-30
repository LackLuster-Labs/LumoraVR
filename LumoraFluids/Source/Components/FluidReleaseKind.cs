// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// A release on a person's ring. Packed into ints, so only append.
public enum FluidReleaseKind
{
    Throw = 0,
    Drop = 1,
    Drip = 2,
    Fling = 3,
    Footprint = 4,
    Wipe = 5,
    Wash = 6,
    Pickup = 7,
    Blob = 8,
    Squeeze = 9,
}
