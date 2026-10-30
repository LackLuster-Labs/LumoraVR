// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;

namespace Lumora.Fluids;

// Draws the systems' stain grids, on the world's local fluid slot: decals on Forward+, quads laid on the surface
// on the Mobile renderer. Stains are coarse surface cells (a footprint adds to its cell), at most 1024 a system.
[ComponentCategory("Hidden")]
public sealed class FluidStainPool : ImplementableComponent
{
    public const int MaxStains = 1024;

    // m, one stain cell
    public const float CellSize = 0.1f;
}
