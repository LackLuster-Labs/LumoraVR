// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A cached per-asset triangle grid: cells of triangle index lists over the mesh's bounds.
// std430, 48 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidTriangleGridHeader
{
    public const int Size = 48;

    // m, collider space
    public float3 Origin;
    // m
    public float CellSize;
    public uint DimX;
    public uint DimY;
    public uint DimZ;
    public uint TriangleStart;
    public uint CellStart;
    public uint IndexStart;
    public uint TriangleCount;
    public uint Flags;
}
