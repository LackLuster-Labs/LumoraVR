// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// An entry on the keeper's report ring. Packed into ints, so only append.
public enum FluidReportKind
{
    Cell = 0,
    PropTouch = 1,
    PropSnap = 2,
    Drained = 3,
    ContainerLitres = 4,
    PendingRemote = 5,
    CoatProposal = 6,
    OriginLitres = 7,
}
