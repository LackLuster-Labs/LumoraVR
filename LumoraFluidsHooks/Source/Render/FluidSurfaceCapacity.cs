// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Fluids.Hooks;

// A tier's surface sizes (plan 11.2): bulk triangles, the cell as a multiple of the spacing, tube sides and drawn
// strands, the radius floor, spray. The bulk is counted in chunks of 32 vertices and 32 quads; surface nets make
// about one vertex a quad, so a chunk is about 64 triangles. Ultra's 192k triangles pass 65,536 vertices and get
// 32-bit indices. Growing tubes also switch to 32-bit indices when needed. -xlinka
public readonly struct FluidSurfaceCapacity
{
    // vertices and quads in one chunk, and chunks in one brick's list (FLUID_CHUNK, FLUID_CHUNK_LIST)
    public const int ChunkSize = 32;
    public const int ChunksPerBrick = 16;
    // The largest tube index buffer whose byte count fits an array; dispatch bounds are checked by the renderer. -xlinka
    public const int MaxTubeVertices = int.MaxValue / 24;
    public const int InitialTubeVertices = 65536;
    private readonly int _tubeVertices;
    public const int MaxSpray = 1024;

    public FluidSurfaceCapacity(int bulkTriangles, float cellScale, int tubeSides, int strands, float radiusFloor, bool spray, bool smoothing, int fieldSlots, int tubeSegments, int tubeVertices = 0)
    {
        BulkTriangles = bulkTriangles;
        CellScale = cellScale;
        TubeSides = tubeSides;
        Strands = strands;
        RadiusFloor = radiusFloor;
        Spray = spray;
        Smoothing = smoothing;
        FieldSlots = fieldSlots;
        TubeSegments = tubeSegments;
        _tubeVertices = tubeVertices;
    }

    public int BulkTriangles { get; }
    // x the spacing
    public float CellScale { get; }
    public int TubeSides { get; }
    public int Strands { get; }
    // m
    public float RadiusFloor { get; }
    public bool Spray { get; }
    public bool Smoothing { get; }
    // dirty bricks extracted per frame
    public int FieldSlots { get; }
    public int TubeSegments { get; }

    public int Chunks => Math.Max(1, BulkTriangles / 2 / ChunkSize);

    public int BulkVertices => Chunks * ChunkSize;

    public int BulkQuads => Chunks * ChunkSize;

    public int Bricks => Math.Max(256, Chunks * 2);

    // The tier reserves a small starting pool. Overflow grows this independently of the bulk surface. -xlinka
    public int TubeVertices => Math.Clamp(_tubeVertices > 0 ? _tubeVertices
        : (int)Math.Min(InitialTubeVertices, (long)Strands * 48 * (TubeSegments + 3) * TubeSides), 1024, MaxTubeVertices);

    public int TubeQuads => TubeVertices;

    public int SprayQuads => Spray ? MaxSpray : 64;

    public int SprayVertices => SprayQuads * 4;

    public static FluidSurfaceCapacity For(FluidGpuTier tier) => tier switch
    {
        FluidGpuTier.Ultra => new FluidSurfaceCapacity(192000, 0.5f, 12, 64, 0f, true, true, 384, 4),
        FluidGpuTier.High => new FluidSurfaceCapacity(96000, 0.75f, 8, 48, 0f, true, true, 256, 4),
        FluidGpuTier.Medium => new FluidSurfaceCapacity(32000, 1.0f, 6, 24, 0.0005f, false, false, 128, 3),
        FluidGpuTier.Low => new FluidSurfaceCapacity(16000, 1.0f, 4, 12, 0.001f, false, false, 96, 2),
        _ => new FluidSurfaceCapacity(16000, 1.0f, 4, 8, 0.001f, false, false, 64, 2),
    };
}
