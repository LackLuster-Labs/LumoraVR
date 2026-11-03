// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Dirty bricks and slab allocation. pc.pass picks the step:
//   0 mark     per particle: every brick a moving particle touches is found or inserted in the hash and stamped
//              with the frame. Thread 0 first settles last frame's chunk pops and resets the frame counters.
//   1 alloc    per table entry: a key stamped this frame with no brick takes one; a brick stamped this frame or
//              last (a particle may have just left it), inside a dirty box, deferred, or every brick on an
//              all-dirty frame, takes a field slot and gives its chunks back zeroed. A settled brick is not
//              touched at all. A brick that empties keeps its key with no brick behind it, so the same place
//              coming back finds its own entry; vacant keys are counted.
//   2 clear    per table entry: when the vacant keys passed a quarter of the table last frame (or the CPU asks),
//              empty the table.
//   3 reinsert per brick, same condition: every live brick goes back in with its stamp.
// Pops and pushes on the two free stacks never share a pass, and a pop only ever counts up from the top it saw
// when the pass began, so a failed pop needs no undo. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"
#include "Include/FluidSurface.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_SURF_DECLARE_PUSH
FLUID_SURF_DECLARE_PARAMS
FLUID_SURF_DECLARE_PARTICLES
FLUID_SURF_DECLARE_TABLE
FLUID_SURF_DECLARE_BRICKS
FLUID_SURF_DECLARE_BRICK_CHUNKS
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_STACKS
FLUID_SURF_DECLARE_DIRTY
FLUID_SURF_DECLARE_INDEX
FLUID_SURF_DECLARE_BONDS
FLUID_SURF_DECLARE_LIVE

#include "Include/FluidSurfaceStrand.glsli"
#include "Include/FluidSurfaceCoat.glsli"
#include "Include/FluidSurfaceShape.glsli"

bool inDirtyBox(vec3 lo, vec3 hi)
{
    for (uint i = 0u; i < min(sp.dirtyBoxCount, 2u); i++)
    {
        if (all(lessThanEqual(lo, sp.dirtyBoxMax[i].xyz)) && all(greaterThanEqual(hi, sp.dirtyBoxMin[i].xyz)))
            return true;
    }
    return false;
}

void stamp(uint key)
{
    uint h = fluidBrickHash(key, sp.tableMask);
    for (uint probe = 0u; probe < FLUID_PROBES; probe++)
    {
        uint e = (h + probe) & sp.tableMask;
        uint seen = atomicCompSwap(table[e].x, FLUID_KEY_EMPTY, key);
        if (seen == FLUID_KEY_EMPTY || seen == key)
        {
            atomicMax(table[e].z, sp.frame);
            return;
        }
    }
    atomicAdd(counters[FLUID_SC_PROBE_FAIL], 1u);
}

void markPass()
{
    uint i = gl_GlobalInvocationID.x;
    if (i == 0u)
    {
        counters[FLUID_SC_CHUNK_TOP] -= counters[FLUID_SC_CHUNK_TAKEN];
        counters[FLUID_SC_CHUNK_POPS] = 0u;
        counters[FLUID_SC_CHUNK_TAKEN] = 0u;
        counters[FLUID_SC_DIRTY] = 0u;
        counters[FLUID_SC_COARSE] = 0u;
        counters[FLUID_SC_CHUNK_FAIL] = 0u;
        counters[FLUID_SC_FIELD_FULL] = 0u;
        counters[FLUID_SC_TOMBS] = 0u;
        counters[FLUID_SC_BRICK_FAIL] = 0u;
        counters[FLUID_SC_PROBE_FAIL] = 0u;
        counters[FLUID_SC_EXTRACTED] = 0u;
        counters[FLUID_SC_QUADS] = 0u;
        counters[FLUID_SC_VERTS] = 0u;
        counters[FLUID_SC_TUBE_QUADS_LAST] = counters[FLUID_SC_TUBE_QUADS];
        counters[FLUID_SC_TUBE_QUADS] = 0u;
        counters[FLUID_SC_TUBE_VERTS] = 0u;
        counters[FLUID_SC_TUBE_OVERFLOW] = 0u;
        counters[FLUID_SC_STRANDS] = 0u;
        counters[FLUID_SC_SPRAY_QUADS_LAST] = counters[FLUID_SC_SPRAY_QUADS];
        counters[FLUID_SC_MAP_FOLDS] = 0u;
        counters[FLUID_SC_MAPPED] = 0u;
        counters[FLUID_SC_MAP_DEEP] = 0u;
        counters[FLUID_SC_FRAME] = sp.frame;
        counters[FLUID_SC_REBUILD] = 0u;
    }
    if (i >= FLUID_SURF_LIVE)
        return;
    FluidParticle p = particles[i];
    bool awake = (p.phase & FLUID_PARTICLE_ASLEEP) == 0u;
    bool all = (sp.flags & FLUID_SURF_ALL_DIRTY) != 0u;
    if (!fluidSurfSplats(i, p)) return;
    FluidSurfaceShape shape = fluidSurfShape(i, p);
    if (shape.weight <= 0.0) return;
    vec3 reach = fluidSurfShapeReach(shape);
    vec3 xa = min(shape.centre, shape.centre + shape.trail) - reach;
    vec3 xb = max(shape.centre, shape.centre + shape.trail) + reach;
    if (!awake && !all && !inDirtyBox(xa, xb))
        return;
    if (!fluidSurfSplats(i, p))
        return;
    vec3 ua = (xa - sp.gridOrigin) / sp.cellSize;
    vec3 ub = (xb - sp.gridOrigin) / sp.cellSize;
    // a brick's nodes run from 8b - 2 to 8b + 9
    ivec3 lo = ivec3(ceil((ua - 9.0) / 8.0));
    ivec3 hi = ivec3(floor((ub + 2.0) / 8.0));
    for (int bz = lo.z; bz <= hi.z; bz++)
    for (int by = lo.y; by <= hi.y; by++)
    for (int bx = lo.x; bx <= hi.x; bx++)
    {
        ivec3 b = ivec3(bx, by, bz);
        if (fluidBrickKeyValid(b))
            stamp(fluidBrickKey(b));
    }
}

void freeChunks(uint slot, uint count)
{
    bool wide = (sp.flags & FLUID_SURF_INDEX32) != 0u;
    for (uint k = 0u; k < count; k++)
    {
        uint chunk = brickChunks[slot * FLUID_CHUNK_LIST + k];
        for (uint q = 0u; q < FLUID_CHUNK; q++)
            FLUID_ZERO_QUAD(ib, wide, chunk * FLUID_CHUNK + q)
        uint top = atomicAdd(counters[FLUID_SC_CHUNK_TOP], 1u);
        stacks[sp.brickCapacity + top] = chunk;
    }
}

void allocPass()
{
    uint e = gl_GlobalInvocationID.x;
    if (e > sp.tableMask)
        return;
    uvec4 entry = table[e];
    if (entry.x == FLUID_KEY_EMPTY)
        return;
    if (entry.x == FLUID_KEY_TOMB)
    {
        atomicAdd(counters[FLUID_SC_TOMBS], 1u);
        return;
    }
    uint slot = entry.y;
    bool fresh = false;
    if (slot == FLUID_NONE)
    {
        if (entry.z != sp.frame)
        {
            atomicAdd(counters[FLUID_SC_TOMBS], 1u);
            return;
        }
        uint available = counters[FLUID_SC_BRICK_TOP];
        uint pop = atomicAdd(counters[FLUID_SC_BRICK_POPS], 1u);
        if (pop >= available)
        {
            // no brick free: the key stays vacant and tries again when a particle stamps it
            atomicAdd(counters[FLUID_SC_BRICK_FAIL], 1u);
            return;
        }
        slot = stacks[available - 1u - pop];
        atomicAdd(counters[FLUID_SC_BRICK_TAKEN], 1u);
        table[e].y = slot;
        ivec3 b = fluidBrickOfKey(entry.x);
        FluidSurfaceBrick nb;
        nb.x = b.x;
        nb.y = b.y;
        nb.z = b.z;
        nb.renderer = 0u;
        nb.chunkCount = 0u;
        nb.vertices = 0u;
        nb.tableEntry = e;
        nb.quads = 0u;
        nb.flags = FLUID_BRICK_LIVE | FLUID_BRICK_NEW;
        nb.particles = 0u;
        nb.lastFrame = entry.z;
        nb.fieldSlot = FLUID_NONE;
        nb.cellSize = sp.cellSize;
        nb.extractFlags = 0u;
        nb.reserved1 = 0u;
        nb.reserved2 = 0u;
        bricks[slot] = nb;
        fresh = true;
    }
    FluidSurfaceBrick brick = bricks[slot];
    brick.lastFrame = max(brick.lastFrame, entry.z);
    vec3 lo = sp.gridOrigin + vec3(brick.x, brick.y, brick.z) * (8.0 * sp.cellSize);
    vec3 hi = lo + vec3(8.0 * sp.cellSize);
    bool dirty = fresh
        || entry.z + 1u >= sp.frame
        || (sp.flags & FLUID_SURF_ALL_DIRTY) != 0u
        || (brick.flags & FLUID_BRICK_DEFERRED) != 0u
        || inDirtyBox(lo, hi);
    if (!dirty)
    {
        bricks[slot].lastFrame = brick.lastFrame;
        bricks[slot].fieldSlot = FLUID_NONE;
        return;
    }
    uint f = atomicAdd(counters[FLUID_SC_DIRTY], 1u);
    if (f >= sp.fieldCapacity)
    {
        // more dirty bricks than field slots: this one keeps its old triangles and goes next frame
        bricks[slot].lastFrame = brick.lastFrame;
        bricks[slot].flags = brick.flags | FLUID_BRICK_DEFERRED;
        bricks[slot].fieldSlot = FLUID_NONE;
        atomicAdd(counters[FLUID_SC_FIELD_FULL], 1u);
        return;
    }
    dirtyList[f] = slot;
    freeChunks(slot, brick.chunkCount);
    brick.chunkCount = 0u;
    brick.vertices = 0u;
    brick.quads = 0u;
    brick.particles = 0u;
    brick.fieldSlot = f;
    brick.flags = (brick.flags & ~(FLUID_BRICK_DEFERRED | FLUID_BRICK_COARSE)) | FLUID_BRICK_LIVE;
    bricks[slot] = brick;
}

bool rebuilding()
{
    return counters[FLUID_SC_REBUILD] != 0u || (sp.flags & FLUID_SURF_REBUILD_TABLE) != 0u;
}

void clearPass()
{
    uint e = gl_GlobalInvocationID.x;
    if (e <= sp.tableMask && rebuilding())
        table[e] = uvec4(FLUID_KEY_EMPTY, FLUID_NONE, 0u, 0u);
}

void reinsertPass()
{
    uint s = gl_GlobalInvocationID.x;
    if (s >= sp.brickCapacity || !rebuilding() || (bricks[s].flags & FLUID_BRICK_LIVE) == 0u)
        return;
    uint key = fluidBrickKey(ivec3(bricks[s].x, bricks[s].y, bricks[s].z));
    uint h = fluidBrickHash(key, sp.tableMask);
    for (uint probe = 0u; probe < FLUID_PROBES; probe++)
    {
        uint e = (h + probe) & sp.tableMask;
        if (atomicCompSwap(table[e].x, FLUID_KEY_EMPTY, key) == FLUID_KEY_EMPTY)
        {
            table[e].y = s;
            table[e].z = bricks[s].lastFrame;
            bricks[s].tableEntry = e;
            return;
        }
    }
    atomicAdd(counters[FLUID_SC_PROBE_FAIL], 1u);
}

void main()
{
    if (pc.pass == 0u)
        markPass();
    else if (pc.pass == 1u)
        allocPass();
    else if (pc.pass == 2u)
        clearPass();
    else
        reinsertPass();
}
