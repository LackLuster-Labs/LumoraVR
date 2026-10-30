// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// A request on a person's ring, judged by the host. Packed into ints, so only append.
public enum FluidRequestKind
{
    Remove = 0,
    UndoPour = 1,
    ClearMine = 2,
    Emit = 3,
    AdoptSubstance = 4,
    EnsureLoose = 5,
}
