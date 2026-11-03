// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Splats particles into the field of every dirty brick they reach, volume weighted, in fixed point. Integer sums
// do not care about order, so the same particles give the same field on any frame and in both bricks of a seam.
// Every particle reads the table, asleep ones too: a dirty brick needs all of its particles, not just the movers.
// A particle with few neighbours (a drop, a strand end) splats heavier, or it would fall under the iso level and
// vanish. A fast particle splats along its trail (fluidSurfTrail), so a stream laid down a packet a frame, or
// pulled into drops, draws as one rope; a slow one with few neighbours spreads sideways (fluidSurfSpread), so water
// run out into one thin layer draws as a sheet. Thread 0 first settles the brick pops of the alloc pass and writes
// the nets' dispatch size. -xlinka

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
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_FIELD
FLUID_SURF_DECLARE_BONDS
FLUID_SURF_DECLARE_ARGS
FLUID_SURF_DECLARE_LIVE

#include "Include/FluidSurfaceStrand.glsli"
#include "Include/FluidSurfaceCoat.glsli"
#include "Include/FluidSurfaceShape.glsli"

// A lone particle peaks at about 0.2 of a filled lattice, so it is weighted up to reach the iso level with a
// sphere close to its own volume.
#define FLUID_SOLO_BOOST 3.4
// The kernel's integral grows by 1 + 1.231 L / R swept a length L (its cross-section integrates to 1.231 / R); a
// crowded particle divides by the square root, so a rope grows a little fatter at speed but never doubles.
#define FLUID_TRAIL_SPREAD 1.231

uint findBrick(uint key)
{
    uint h = fluidBrickHash(key, sp.tableMask);
    for (uint probe = 0u; probe < FLUID_PROBES; probe++)
    {
        uvec4 entry = table[(h + probe) & sp.tableMask];
        if (entry.x == key)
            return entry.y;
        if (entry.x == FLUID_KEY_EMPTY)
            return FLUID_NONE;
    }
    return FLUID_NONE;
}

void main()
{
    // Sixteen lanes share each particle's samples without changing the fixed-point sums. -xlinka
    uint invocation = gl_GlobalInvocationID.x + gl_GlobalInvocationID.y * gl_NumWorkGroups.x * gl_WorkGroupSize.x;
    uint i = invocation / 16u;
    int lane = int(invocation % 16u);
    if (i == 0u && lane == 0)
    {
        counters[FLUID_SC_BRICK_TOP] -= counters[FLUID_SC_BRICK_TAKEN];
        counters[FLUID_SC_BRICK_POPS] = 0u;
        counters[FLUID_SC_BRICK_TAKEN] = 0u;
        uint groups = min(counters[FLUID_SC_DIRTY], sp.fieldCapacity);
        counters[FLUID_SC_REBUILD] = counters[FLUID_SC_TOMBS] * 4u > sp.tableMask ? 1u : 0u;
        netsArgs[0] = groups;
        netsArgs[1] = 1u;
        netsArgs[2] = 1u;
    }
    // a settled frame has no field to fill: nobody walks the table
    if (i >= FLUID_SURF_LIVE || counters[FLUID_SC_DIRTY] == 0u)
        return;
    FluidParticle p = particles[i];
    if (!fluidSurfSplats(i, p))
        return;

    FluidSurfaceShape shape = fluidSurfShape(i, p);
    if (shape.weight <= 0.0) return;
    vec3 x = shape.centre;
    vec3 u = (x - sp.gridOrigin) / sp.cellSize;
    vec3 reach = fluidSurfShapeReach(shape) / sp.cellSize;
    vec3 trail = shape.trail / sp.cellSize;
    vec3 ua = min(u, u + trail) - reach;
    vec3 ub = max(u, u + trail) + reach;
    float speed = length(p.velocity);
    vec4 tint = fluidUnpackTint(p.tint);
    float strand = fluidPhaseOf(p.phase) == FLUID_PHASE_STRAND ? 1.0 : 0.0;
    float channel[7] = float[7](1.0, tint.r, tint.g, tint.b, speed, p.wetness, strand);

    ivec3 lo = ivec3(ceil((ua - 9.0) / 8.0));
    ivec3 hi = ivec3(floor((ub + 2.0) / 8.0));
    for (int bz = lo.z; bz <= hi.z; bz++)
    for (int by = lo.y; by <= hi.y; by++)
    for (int bx = lo.x; bx <= hi.x; bx++)
    {
        ivec3 b = ivec3(bx, by, bz);
        if (!fluidBrickKeyValid(b))
            continue;
        uint slot = findBrick(fluidBrickKey(b));
        if (slot == FLUID_NONE)
            continue;
        uint fieldSlot = bricks[slot].fieldSlot;
        if (fieldSlot == FLUID_NONE)
            continue;
        if (lane == 0) atomicAdd(bricks[slot].particles, 1u);
        uint base = fieldSlot * uint(FLUID_FIELD_INTS);
        // Distances run from the grid-wide node, an exact integer, so both bricks of a seam weigh a shared node
        // with the same bits. A node at the edge of the range sits at the kernel's edge, where the weight rounds
        // to 0 in either brick, so the range needs no margin.
        ivec3 origin = b * FLUID_BRICK_CELLS;
        vec3 local = u - vec3(origin);
        ivec3 n0 = max(ivec3(ceil(ua - vec3(origin))), ivec3(-2));
        ivec3 n1 = min(ivec3(floor(ub - vec3(origin))), ivec3(9));
        ivec3 size = max(n1 - n0 + 1, ivec3(0));
        int samples = size.x * size.y * size.z;
        for (int k = lane; k < samples; k += 16)
        {
            int nx = n0.x + k % size.x;
            int ny = n0.y + (k / size.x) % size.y;
            int nz = n0.z + k / (size.x * size.y);
            vec3 position = sp.gridOrigin + vec3(origin + ivec3(nx, ny, nz)) * sp.cellSize;
            int iw = int(fluidSurfShapeWeight(shape, position) * sp.fieldScale + 0.5);
            if (iw != 0)
                atomicAdd(field[base + fluidFieldNode(ivec3(nx, ny, nz))], iw);
        }
        // Attributes sample the same support as density, including stretched and subcell sheets. -xlinka
        ivec3 a0 = max(ivec3(ceil((ua - vec3(origin) + 2.0) * 0.5)), ivec3(0));
        ivec3 a1 = min(ivec3(floor((ub - vec3(origin) + 2.0) * 0.5)), ivec3(FLUID_ATTR_DIM - 1));
        size = max(a1 - a0 + 1, ivec3(0));
        samples = size.x * size.y * size.z;
        for (int k = lane; k < samples; k += 16)
        {
            ivec3 node = a0 + ivec3(k % size.x, (k / size.x) % size.y, k / (size.x * size.y));
            vec3 position = sp.gridOrigin + vec3(origin + node * 2 - 2) * sp.cellSize;
            float w = fluidSurfShapeWeight(shape, position);
            if (w <= 0.0) continue;
            uint at = base + fluidAttrNode(node);
            for (int c = 0; c < 7; c++)
            {
                int iv = int(w * channel[c] * sp.fieldScale + 0.5);
                if (iv != 0) atomicAdd(field[at + uint(c)], iv);
            }
            // An undeformed sample contributes exactly zero to all nine mapping channels. -xlinka
            if (shape.compression != 1.0)
            {
                mat3 compression;
                vec3 displacement;
                fluidSurfShapeMap(shape, position, compression, displacement);
                float mapping[9] = float[9](compression[0][0], compression[1][1], compression[2][2],
                    compression[0][1], compression[0][2], compression[1][2], displacement.x, displacement.y, displacement.z);
                for (uint c = 0u; c < 9u; c++)
                {
                    int iv = int(round(w * mapping[c] * sp.fieldScale));
                    if (iv != 0) atomicAdd(field[at + 7u + c], iv);
                }
            }
        }
    }
}
