// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids.Hooks;

// One renderer's surface parameters as the surface kernels read them: FluidSurfaceParams in
// Compute/Include/FluidSurface.glsli, std430, 304 B. Offsets are pinned here and checked against the shader by
// a test that has a kernel echo every field back.
[StructLayout(LayoutKind.Explicit, Size = Size)]
public unsafe struct FluidSurfaceParams
{
    public const int Size = 304;

    public const uint AllDirty = 1;
    public const uint Smooth = 2;
    public const uint Index32 = 4;
    public const uint Strands = 8;
    public const uint Spray = 16;
    public const uint RebuildTable = 32;
    public const uint BondsBySlot = 64;
    public const uint ForceCoarse = 128;
    public const uint SkinContacts = 256;

    // particle frame to renderer local, rows of a 3x4
    [FieldOffset(0)] public float ToLocal00;
    [FieldOffset(4)] public float ToLocal01;
    [FieldOffset(8)] public float ToLocal02;
    [FieldOffset(12)] public float ToLocal03;
    [FieldOffset(16)] public float ToLocal10;
    [FieldOffset(20)] public float ToLocal11;
    [FieldOffset(24)] public float ToLocal12;
    [FieldOffset(28)] public float ToLocal13;
    [FieldOffset(32)] public float ToLocal20;
    [FieldOffset(36)] public float ToLocal21;
    [FieldOffset(40)] public float ToLocal22;
    [FieldOffset(44)] public float ToLocal23;
    // renderer local, m: where brick (0, 0, 0) puts its node 0
    [FieldOffset(48)] public float GridOriginX;
    [FieldOffset(52)] public float GridOriginY;
    [FieldOffset(56)] public float GridOriginZ;
    // m
    [FieldOffset(60)] public float CellSize;
    // m
    [FieldOffset(64)] public float SplatRadius;
    [FieldOffset(68)] public float Iso;
    // fixed point units for a field value of 1
    [FieldOffset(72)] public float FieldScale;
    // 0..1
    [FieldOffset(76)] public float Smoothing;
    [FieldOffset(80)] public uint Frame;
    [FieldOffset(84)] public uint Flags;
    [FieldOffset(88)] public uint ParticleCount;
    // FluidLayout.None = every system
    [FieldOffset(92)] public uint System;
    [FieldOffset(96)] public uint BrickCapacity;
    [FieldOffset(100)] public uint TableMask;
    [FieldOffset(104)] public uint FieldCapacity;
    [FieldOffset(108)] public uint ChunkCapacity;
    [FieldOffset(112)] public uint ChunksPerBrick;
    [FieldOffset(116)] public uint VertexCapacity;
    [FieldOffset(120)] public uint QuadCapacity;
    [FieldOffset(124)] public uint NormalWords;
    [FieldOffset(128)] public uint TubeSides;
    [FieldOffset(132)] public uint TubeSegments;
    [FieldOffset(136)] public uint TubeVertexCapacity;
    [FieldOffset(140)] public uint TubeQuadCapacity;
    [FieldOffset(144)] public uint TubeNormalWords;
    // 0 = no cap
    [FieldOffset(148)] public uint MaxStrands;
    [FieldOffset(152)] public uint BondSlots;
    [FieldOffset(156)] public uint SprayCount;
    // m, the tier's drawn radius floor
    [FieldOffset(160)] public float MinRadius;
    // m, half the spacing
    [FieldOffset(164)] public float MaxRadius;
    // m
    [FieldOffset(168)] public float SprayRadius;
    [FieldOffset(172)] public uint SprayVertexCapacity;
    [FieldOffset(176)] public uint SprayQuadCapacity;
    [FieldOffset(180)] public uint SprayNormalWords;
    [FieldOffset(184)] public uint DirtyBoxCount;
    // neighbours under which a particle splats heavier; 0 = never
    [FieldOffset(188)] public uint SoloNeighbours;
    // 256 origin bits
    [FieldOffset(192)] public fixed uint HiddenOrigins[8];
    [FieldOffset(224)] public fixed float DirtyBoxMin[8];
    [FieldOffset(256)] public fixed float DirtyBoxMax[8];
    // s: a fast particle splats along where it was this long ago (fluidSurfTrail); 0 = no trails
    [FieldOffset(288)] public float TrailTime;
    // m, the longest trail
    [FieldOffset(292)] public float TrailMax;
    // m/s under which a particle draws no trail
    [FieldOffset(296)] public float TrailSpeed;
    [FieldOffset(300)] public uint Reserved0;
}
