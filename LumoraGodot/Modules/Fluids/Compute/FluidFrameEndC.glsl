// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Frame end C, one workgroup:
//   1  accepts each valid contact from last frame's anchor list, using its particle's existing anchor slot.
//      Anchoring lands one frame after candidacy; wetting and adhesive strength decide what sticks. -xlinka
//   2  scans this frame's chunk counts of anchor and refine candidates, and grants refinement what the pool and
//      the free stack allow; D places the midpoints at Live + rank.
//   3  steps every sleep region: a region nobody fast touched gains quiet time, any other starts over.
// Push constant Arg is the tier's refine pool. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_SCAN_THREADS, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_ANCHORS
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_COLLIDERS
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_CANDIDATES
FLUID_DECLARE_REGIONS
FLUID_DECLARE_ARGS
FLUID_DECLARE_PUSH

shared uint sSum[FLUID_SCAN_THREADS];

// Exclusive scan of src[0..count) into dst by the whole workgroup; returns the total.
uint scanChunks(uint src, uint dst, uint count)
{
    uint t = gl_LocalInvocationIndex;
    uint per = (count + FLUID_SCAN_THREADS - 1u) / FLUID_SCAN_THREADS;
    uint first = t * per;
    uint last = min(first + per, count);
    uint sum = 0u;
    for (uint c = first; c < last; c++)
        sum += candidates.v[src + c];
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
    for (uint c = first; c < last; c++)
    {
        candidates.v[dst + c] = run;
        run += candidates.v[src + c];
    }
    uint total = sSum[FLUID_SCAN_THREADS - 1u];
    barrier();
    return total;
}

void main()
{
    uint t = gl_LocalInvocationIndex;

    uint listed = min(candidates.v[FLUID_K_TOTALS], FLUID_K_LIST_CAP);
    for (uint k = t; k < listed; k += FLUID_SCAN_THREADS)
    {
        uint e = FLUID_K_LIST + k * FLUID_K_LIST_STRIDE;
        uint id = candidates.v[e];
        uint kind = candidates.v[e + 1u];
        uint target = candidates.v[e + 2u];
        if (id == FLUID_NONE)
            continue;
        uint slot = idToSlot.v[id];
        if (slot == FLUID_NONE || fluidPhaseOf(pa.p[slot].phase) == FLUID_PHASE_DEAD)
            continue;
        if ((anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE)
            continue;
        // A candidate waited one frame too; resolve its original collider before accepting it. -xlinka
        if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
        {
            uint identity = candidates.v[e + 3u];
            if (identity != 0u && identity != FLUID_NONE
                && (target >= hdr.h.colliders || colliders.c[target].reserved1 != identity))
            {
                target = FLUID_NONE;
                for (uint c = 0u; c < hdr.h.colliders; c++)
                    if (colliders.c[c].reserved1 == identity)
                    {
                        target = c;
                        break;
                    }
            }
            if (target >= hdr.h.colliders)
                continue;
            if ((colliders.c[target].flags & (FLUID_COLLIDER_DYNAMIC | FLUID_COLLIDER_MOVED)) != 0u)
                kind = FLUID_ANCHOR_RIGID;
        }
        // Each wet contact already owns an anchor slot; small old contacts must not block fresh coating. -xlinka
        FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(pa.p[slot].material)];
        float surface = 1.0;
        if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
            surface = target < hdr.h.colliders ? colliders.c[target].stickiness : 0.0;
        else
            surface = (target >> 8) < hdr.h.people ? people.p[target >> 8].stickiness : 0.0;
        float strength = sub.anchorStrength * surface * (0.85 + 0.3 * fluidRandom(id, 0xA11C4u));
        if (strength <= 0.0)
            continue;
        anchors.a[id].kindAge = kind;
        anchors.a[id].target = target;
        anchors.a[id].triangle = candidates.v[e + 3u];
        anchors.a[id].normal = candidates.v[e + 4u];
        anchors.a[id].point = vec3(uintBitsToFloat(candidates.v[e + 5u]), uintBitsToFloat(candidates.v[e + 6u]), uintBitsToFloat(candidates.v[e + 7u]));
        anchors.a[id].strength = strength;
    }
    barrier();

    uint awake = counters.v[FLUID_C_AWAKE];
    uint chunks = min((awake + FLUID_GROUP_SIZE - 1u) / FLUID_GROUP_SIZE, FLUID_K_CHUNKS_MAX);
    uint anchorTotal = scanChunks(FLUID_K_ANCHOR_COUNTS, FLUID_K_ANCHOR_SCAN, chunks);
    uint refineTotal = scanChunks(FLUID_K_REFINE_COUNTS, FLUID_K_REFINE_SCAN, chunks);
    if (t == 0u)
    {
        candidates.v[FLUID_K_TOTALS] = min(anchorTotal, FLUID_K_LIST_CAP);
        candidates.v[FLUID_K_TOTALS + 1u] = refineTotal;
        uint live = counters.v[FLUID_C_LIVE];
        uint free = counters.v[FLUID_C_FREE];
        uint pool = counters.v[FLUID_C_POOL];
        uint room = pc.c.arg > pool ? pc.c.arg - pool : 0u;
        uint accepted = min(refineTotal, min(room, free));
        counters.v[FLUID_C_REFINE_BASE] = live;
        counters.v[FLUID_C_REFINE_FREE_TOP] = free;
        counters.v[FLUID_C_REFINE_ACCEPTED] = accepted;
        counters.v[FLUID_C_POOL] = pool + accepted;
        counters.v[FLUID_C_FREE] = free - accepted;
        counters.v[FLUID_C_LIVE] = live + accepted;
        counters.v[FLUID_C_POOL_EMPTY] = refineTotal > accepted ? 1u : 0u;
        // B has read the ligament list; the next frame's A starts a fresh one
        candidates.v[FLUID_K_LIGAMENT_COUNT] = 0u;
        args.v[FLUID_ARGS_LIVE] = (live + accepted + FLUID_GROUP_SIZE - 1u) / FLUID_GROUP_SIZE;
        args.v[FLUID_ARGS_LIVE + 1u] = 1u;
        args.v[FLUID_ARGS_LIVE + 2u] = 1u;
    }

    float dtF = hdr.h.dtFrame;
    for (uint r = t; r < FLUID_REGION_TABLE; r += FLUID_SCAN_THREADS)
    {
        uint flags = regions.r[r].awake;
        float quiet = (flags & 3u) != 0u ? 0.0 : uintBitsToFloat(regions.r[r].quietFrames) + dtF;
        regions.r[r].quietFrames = floatBitsToUint(min(quiet, 1e6));
        regions.r[r].awake = 0u;
        regions.r[r].maxSpeedBits = 0u;
    }
}
