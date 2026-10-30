// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// Volume the keeper's simulation anchored to a remote person that still counts in its home cell.
// std430, 16 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidPendingRemote
{
    public const int Size = 16;

    public uint Person;
    public uint Cell;
    // nL
    public int VolumeNl;
    public uint System;
}
