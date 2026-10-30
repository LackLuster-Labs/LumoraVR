// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// The start of the summary read back each frame. Every section sits at a fixed offset (FluidLayout) and the header
// says how many records each holds this time.
// std430, 128 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 128)]
public struct FluidSummaryHeader
{
    public const int Size = 128;

    // Sequence of the frame it describes
    public uint Frame;
    public uint Flags;
    public uint Awake;
    public uint Live;
    public uint Contacts;
    public uint Limbs;
    public uint Bakes;
    public uint Pending;
    public uint Impulses;
    public uint Volumes;
    public uint Systems;
    public uint Queries;
    public uint Props;
    public uint Bins;
    public uint BinCursor;
    public uint BinTotal;
    // m/s
    public float Vmax;
    // J
    public float KineticEnergy;
    public uint NanCount;
    public uint Overflow;
    public uint NeighbourOverflow;
    public uint SlabOverflow;
    public uint PoolEmpty;
    public uint Reserved0;
    // The submitted block that produced this summary; Reserved0 belongs to bin pages. -xlinka
    public uint SubmittedSequence;
}
