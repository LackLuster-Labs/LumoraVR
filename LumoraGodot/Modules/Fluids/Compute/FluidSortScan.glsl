// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Sort, step 2: the exclusive scan of the cell counts, one 1024-cell block per workgroup. Each cell's Start is its
// offset inside its block and each block's sum goes in the grid's tail; the scatter adds the block offsets up in
// shared memory. One level, so 32k particles (a 128k-cell grid) cost one dispatch, not one workgroup walking it all.
// -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_SCAN_THREADS, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_GRID
FLUID_DECLARE_HEADER
FLUID_DECLARE_PUSH

shared uint sSum[FLUID_SCAN_THREADS];

void main()
{
    uint t = gl_LocalInvocationIndex;
    uint block = gl_WorkGroupID.x;
    uint tableSize = hdr.h.gridSize;
    uint cells = fluidGridCells(tableSize);
    uint per = FLUID_SCAN_BLOCK / FLUID_SCAN_THREADS;
    uint first = block * FLUID_SCAN_BLOCK + t * per;
    uint counts[4];
    uint sum = 0u;
    for (uint k = 0u; k < 4u; k++)
    {
        uint c = first + k;
        counts[k] = c < cells ? grid.c[c].count : 0u;
        sum += counts[k];
    }
    sSum[t] = sum;
    barrier();
    for (uint off = 1u; off < FLUID_SCAN_THREADS; off <<= 1)
    {
        uint v = t >= off ? sSum[t - off] : 0u;
        barrier();
        sSum[t] += v;
        barrier();
    }
    uint run = sSum[t] - sum;
    for (uint k = 0u; k < 4u; k++)
    {
        uint c = first + k;
        if (c < cells)
            grid.c[c].start = run;
        run += counts[k];
    }
    if (t == FLUID_SCAN_THREADS - 1u)
        grid.c[cells + block].count = sSum[t];
}
