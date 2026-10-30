// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A ledger cell with its system and table index, both ways: changed cells uploaded for reconcile, and the keeper's
// bins coming back in the summary. Cell is the int4 of FluidSystem.Cells, bit for bit.
// std430, 32 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidLedgerEntry
{
    public const int Size = 32;

    public uint System;
    // ledger array index, None for a new cell
    public uint Index;
    // RGBA8
    public uint Tint;
    public uint Flags;
    public int4 Cell;
}
