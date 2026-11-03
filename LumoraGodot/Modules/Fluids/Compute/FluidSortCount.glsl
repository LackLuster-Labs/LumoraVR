// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Sort, step 1: every slot's cell key and the per-cell counts. Push constant Arg names the counter holding how many
// slots there are (Presort on the frame's first sort, Live on a mid-frame re-sort); Pass 0 marks the first sort,
// whose thread 0 also settles the free stack after the spawns and the growth. The grid's counts are cleared before
// this runs. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_IDA
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_BONDS
FLUID_DECLARE_BY_ID
FLUID_DECLARE_ACC
FLUID_DECLARE_GRID
FLUID_DECLARE_KEYS
FLUID_DECLARE_HEADER
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_PUSH

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i == 0u && pc.c.pass == 0u)
    {
        counters.v[FLUID_C_FREE] = counters.v[FLUID_C_FREE] + counters.v[FLUID_C_GROW] - counters.v[FLUID_C_SPAWNED];
        counters.v[FLUID_C_GROW] = 0u;
    }
    uint n = counters.v[pc.c.arg];
    if (i >= n)
        return;
    if (pc.c.pass == 0u)
    {
        // Clear incoming links while dead ids still resolve to their old slots. The cell pass recycles them next;
        // this also visits sleeping rows and one-sided links which the dead particle cannot find. -xlinka
        uint id = idA.v[i];
        // A sleeping label can still own an awake thread's snap choice. -xlinka
        byId.v[id * FLUID_BY_ID_STRIDE + FLUID_ID_SNAP_BEST] = 0u;
        bool dead = fluidPhaseOf(pa.p[i].phase) == FLUID_PHASE_DEAD;
        // Strand labels share stable ids too; a recycled label must not join unrelated threads. -xlinka
        uint label = acc.a[id].label;
        uint labelSlot = label < hdr.h.capacity ? idToSlot.v[label] : FLUID_NONE;
        if (dead || labelSlot == FLUID_NONE || labelSlot >= n || fluidPhaseOf(pa.p[labelSlot].phase) == FLUID_PHASE_DEAD)
        {
            acc.a[id].label = id;
            byId.v[id * FLUID_BY_ID_STRIDE + FLUID_ID_LABEL_NEXT] = id;
        }
        for (uint k = 0u; k < hdr.h.bondSlots; k++)
        {
            uint at = id * hdr.h.bondSlots + k;
            uint partner = bonds.b[at].partner;
            if (partner == FLUID_NONE)
                continue;
            uint ps = partner < hdr.h.capacity ? idToSlot.v[partner] : FLUID_NONE;
            if (dead || ps == FLUID_NONE || ps >= n || fluidPhaseOf(pa.p[ps].phase) == FLUID_PHASE_DEAD)
            {
                bonds.b[at].partner = FLUID_NONE;
                bonds.b[at].lambda = 0.0;
            }
        }
    }
    uint key = fluidSortKey(pa.p[i], hdr.h.invCellSize, hdr.h.gridSize);
    keys.v[i] = key;
    atomicAdd(grid.c[key].count, 1u);
}
