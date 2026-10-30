// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// What one GPU frame runs with, uploaded at FramePreDraw with the rest of the frame block. Counts are of the
// uploaded arrays; Live and Awake are the GPU's own, written back by the frame end.
// std430, 256 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
public struct FluidFrameHeader
{
    public const int Size = 256;

    // m/s2, world
    public float3 Gravity;
    // s, already capped at 1/30
    public float DtFrame;
    // s
    public float DtSubstep;
    // s, World.Time: world time
    public float Time;
    // m
    public float CellSize;
    public float InvCellSize;
    public uint Live;
    public uint Awake;
    public uint Capacity;
    public uint AwakeCapacity;
    public uint Substances;
    public uint Systems;
    public uint Colliders;
    public uint Commands;
    public uint Bones;
    public uint Proxies;
    public uint People;
    public uint Frames;
    public uint ContactVertices;
    public uint ContactTriangles;
    public uint SoftSpheres;
    public uint Queries;
    public uint Spots;
    public uint ResidueBones;
    public uint LedgerCells;
    public uint LedgerChanges;
    // FluidGpuTier
    public uint Tier;
    public uint Flags;
    public uint Sequence;
    public uint Substeps;
    public uint BondSlots;
    public uint MaxNeighbours;
    public uint GridSize;
    public uint GridMask;
    // m
    public float Skin;
    public float SleepScale;
    public uint HiddenOrigins;
    public uint Reserved0;
    // FluidForceRecords this frame
    public uint Forces;
}
