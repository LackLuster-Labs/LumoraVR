// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// Per particle scratch for the frame end: thinning moves volume between link ends as integer nanolitres, so the
// order of the atomic adds never changes the result; the strand label converges one round a frame.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidAccumulator
{
    public const int Size = 16;

    // nL, applied in the next pass
    public int VolumeNl;
    // smallest id along the chain
    public uint Label;
    // frames in the strand phase
    public uint StrandAge;
    // refine claim, candidates only
    public uint Claim;
}
