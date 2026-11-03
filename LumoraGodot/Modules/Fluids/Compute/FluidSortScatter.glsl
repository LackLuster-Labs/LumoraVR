// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Sort, step 3: every slot into its cell's range of the B arrays. Each workgroup adds the block sums up in shared
// memory first. Workgroup 0's first thread also publishes what the sort found: the live, awake and dead counts, the
// block starts for the passes after this one, the indirect args every later kernel dispatches with, and the free
// stack's base for the dead ids the order pass pushes. Push constant Arg names the slot counter, as in the count
// pass. The sort's own passes dispatch over the capacity and stop at that counter: they run before anything this
// frame could have written their args. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_PB
FLUID_DECLARE_IDA
FLUID_DECLARE_IDB
FLUID_DECLARE_GRID
FLUID_DECLARE_KEYS
FLUID_DECLARE_CURSOR
FLUID_DECLARE_HEADER
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_ARGS
FLUID_DECLARE_PUSH

shared uint sBlockStart[512];

uint groupsFor(uint n)
{
    return (n + FLUID_GROUP_SIZE - 1u) / FLUID_GROUP_SIZE;
}

void main()
{
    uint t = gl_LocalInvocationIndex;
    uint i = gl_GlobalInvocationID.x;
    uint tableSize = hdr.h.gridSize;
    uint cells = fluidGridCells(tableSize);
    uint blocks = min(fluidScanBlocks(tableSize), 512u);
    if (t == 0u)
    {
        uint run = 0u;
        for (uint b = 0u; b < blocks; b++)
        {
            sBlockStart[b] = run;
            run += grid.c[cells + b].count;
        }
    }
    barrier();

    if (gl_WorkGroupID.x == 0u && t == 0u)
    {
        for (uint b = 0u; b < blocks; b++)
            grid.c[cells + b].start = sBlockStart[b];
        uint dead = 2u * tableSize;
        uint awake = sBlockStart[tableSize / FLUID_SCAN_BLOCK] + grid.c[tableSize].start;
        uint live = sBlockStart[dead / FLUID_SCAN_BLOCK] + grid.c[dead].start;
        uint deadCount = grid.c[dead].count;
        awake = min(awake, hdr.h.awakeCapacity);
        counters.v[FLUID_C_LIVE] = live;
        counters.v[FLUID_C_AWAKE] = awake;
        counters.v[FLUID_C_DEAD] = deadCount;
        if (pc.c.pass == 0u)
        {
            counters.v[FLUID_C_FREE_BASE] = counters.v[FLUID_C_FREE];
            counters.v[FLUID_C_FREE] = counters.v[FLUID_C_FREE] + deadCount;
        }
        args.v[FLUID_ARGS_LIVE] = groupsFor(live);
        args.v[FLUID_ARGS_LIVE + 1u] = 1u;
        args.v[FLUID_ARGS_LIVE + 2u] = 1u;
        args.v[FLUID_ARGS_AWAKE] = groupsFor(awake);
        args.v[FLUID_ARGS_AWAKE + 1u] = 1u;
        args.v[FLUID_ARGS_AWAKE + 2u] = 1u;
        args.v[FLUID_ARGS_AWAKE_LANES] = groupsFor(awake * FLUID_LANES);
        args.v[FLUID_ARGS_AWAKE_LANES + 1u] = 1u;
        args.v[FLUID_ARGS_AWAKE_LANES + 2u] = 1u;
    }

    uint n = counters.v[pc.c.arg];
    if (i >= n)
        return;
    uint key = keys.v[i];
    uint slot = sBlockStart[key / FLUID_SCAN_BLOCK] + grid.c[key].start + atomicAdd(cursor.v[key], 1u);
    pb.p[slot] = pa.p[i];
    idB.v[slot] = idA.v[i];
}
