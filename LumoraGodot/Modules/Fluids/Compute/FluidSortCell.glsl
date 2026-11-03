// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Sort, step 4, two passes on push constant Pass:
//   0  order: inside its cell every slot takes the rank of its id, back into the A arrays, and rewrites the id to
//      slot map. That makes the slot order, every neighbour list and so every float sum after it the same on every
//      run. Dead slots push their ids onto the free stack in that same order. Arg names the slot counter.
//   1  lists: FLUID_LANES threads per awake slot walk the 27 cells around it (awake and asleep halves of each) and
//      write the slots within h + skin, capped at FLUID_MAX_NEIGHBOURS. Each lane counts its share, the lanes scan
//      their counts in shared memory, and a second walk writes each lane's hits at its offset, so the order is
//      lane, then cell, then slot: fixed.
// -xlinka

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
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_GRID
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_HEADER
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_FREE_IDS
FLUID_DECLARE_PUSH

shared uint sLaneCount[FLUID_GROUP_SIZE];
shared uint sCellHashes[FLUID_GROUP_SIZE / FLUID_LANES * 27];

// A clear-all can put tens of thousands of slots in the dead bucket; ranking them by id would be quadratic, and
// their order only decides which free id comes back first, so past this they keep the scatter's order.
#define DEAD_RANK_LIMIT 4096u

uint cellStart(uint key, uint cells)
{
    return grid.c[cells + key / FLUID_SCAN_BLOCK].start + grid.c[key].start;
}

void orderPass(uint i)
{
    uint n = counters.v[pc.c.arg];
    if (i >= n)
        return;
    uint tableSize = hdr.h.gridSize;
    uint cells = fluidGridCells(tableSize);
    FluidParticle q = pb.p[i];
    uint key = fluidSortKey(q, hdr.h.invCellSize, tableSize);
    uint start = cellStart(key, cells);
    uint count = grid.c[key].count;
    uint id = idB.v[i];
    uint rank;
    bool dead = key == 2u * tableSize;
    if (dead && count > DEAD_RANK_LIMIT)
        rank = i - start;
    else
    {
        rank = 0u;
        for (uint j = start; j < start + count; j++)
            rank += idB.v[j] < id ? 1u : 0u;
    }
    uint dst = start + rank;
    pa.p[dst] = q;
    idA.v[dst] = id;
    if (dead)
    {
        idToSlot.v[id] = FLUID_NONE;
        freeIds.v[counters.v[FLUID_C_FREE_BASE] + (dst - start)] = id;
    }
    else
    {
        idToSlot.v[id] = dst;
        slots.v[dst * FLUID_SLOT_STRIDE] = vec4(0.0);
        slots.v[dst * FLUID_SLOT_STRIDE + 1u] = vec4(0.0);
        slots.v[dst * FLUID_SLOT_STRIDE + 2u] = vec4(0.0);
        slots.v[dst * FLUID_SLOT_STRIDE + 3u] = vec4(0.0);
    }
}

void listsPass()
{
    uint gid = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    uint i = gid / FLUID_LANES;
    uint lane = gid % FLUID_LANES;
    uint awake = counters.v[FLUID_C_AWAKE];
    bool live = i < awake;
    uint tableSize = hdr.h.gridSize;
    uint cells = fluidGridCells(tableSize);
    uint mask = tableSize - 1u;
    vec3 x = live ? pa.p[i].position : vec3(0.0);
    uint frame = live ? fluidFrameOf(pa.p[i].material) : 0u;
    ivec3 c = fluidCellOf(x, hdr.h.invCellSize);
    float r2 = hdr.h.cellSize * hdr.h.cellSize;
    uint hits = 0u;
    uint total = 0u;
    // the asleep half is walked only when something sleeps
    uint halves = counters.v[FLUID_C_AWAKE] < counters.v[FLUID_C_LIVE] ? 2u : 1u;
    bool oneFrame = hdr.h.frames <= 1u;
    uint hashBase = (lid / FLUID_LANES) * 27u;
    for (uint k = lane; k < 27u; k += FLUID_LANES)
    {
        ivec3 o = ivec3(int(k % 3u) - 1, int((k / 3u) % 3u) - 1, int(k / 9u) - 1);
        sCellHashes[hashBase + k] = fluidHashCell(c + o, frame, mask);
    }
    barrier();
    uint uniqueCells = 0u;
    for (uint k = lane; k < 27u; k += FLUID_LANES)
    {
        bool duplicate = false;
        for (uint m = 0u; m < k; m++)
            duplicate = duplicate || sCellHashes[hashBase + m] == sCellHashes[hashBase + k];
        if (!duplicate) uniqueCells |= 1u << k;
    }

    for (uint walk = 0u; walk < 2u; walk++)
    {
        uint base = 0u;
        if (walk == 1u)
        {
            sLaneCount[lid] = hits;
            barrier();
            for (uint l = lid - lane; l < lid; l++)
                base += sLaneCount[l];
            total = base;
            for (uint l = lid; l < lid - lane + FLUID_LANES; l++)
                total += sLaneCount[l];
            barrier();
        }
        uint written = 0u;
        for (uint k = lane; live && k < 27u; k += FLUID_LANES)
        {
            if ((uniqueCells & (1u << k)) == 0u)
                continue;
            uint h = sCellHashes[hashBase + k];
            for (uint half_ = 0u; half_ < halves; half_++)
            {
                uint key = h + half_ * tableSize;
                uint start = cellStart(key, cells);
                uint end = start + grid.c[key].count;
                for (uint j = start; j < end; j++)
                {
                    // the frame test reads a second 32-byte sector of the particle: skipped with one frame
                    vec3 d = pa.p[j].position - x;
                    if (j == i || dot(d, d) >= r2 || (oneFrame ? false : fluidFrameOf(pa.p[j].material) != frame))
                        continue;
                    if (walk == 0u)
                        hits++;
                    else
                    {
                        if (base + written < FLUID_MAX_NEIGHBOURS)
                            lists.v[i * FLUID_MAX_NEIGHBOURS + base + written] = j;
                        written++;
                    }
                }
            }
        }
    }
    if (live && lane == 0u)
    {
        uint count = min(total, uint(FLUID_MAX_NEIGHBOURS));
        slots.v[i * FLUID_SLOT_STRIDE].z = uintBitsToFloat(count);
        if (total > uint(FLUID_MAX_NEIGHBOURS))
            atomicAdd(counters.v[FLUID_C_NEIGHBOUR_OVERFLOW], total - uint(FLUID_MAX_NEIGHBOURS));
    }
}

void main()
{
    if (pc.c.pass == 0u)
        orderPass(gl_GlobalInvocationID.x);
    else
        listsPass();
}
