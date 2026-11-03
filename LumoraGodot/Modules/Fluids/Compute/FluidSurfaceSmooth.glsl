// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// One Laplacian pass over the bulk surface (High and up), one workgroup per dirty brick, the same indirect count
// as the nets. Each emitted vertex moves toward the mean of the vertices in its six neighbour cells; the nets
// left every cell -2..8 in the scratch, so a seam vertex sees the same neighbours in both bricks and both copies
// land on the same point. The normal and attributes are read again at the moved point, then the slot's field is
// zeroed for the next splat. Bricks the nets extracted coarse, or could not place, only get their slot zeroed.
// -xlinka

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
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_BRICKS
FLUID_SURF_DECLARE_BRICK_CHUNKS
FLUID_SURF_DECLARE_DIRTY
FLUID_SURF_DECLARE_FIELD
FLUID_SURF_DECLARE_SCRATCH
FLUID_SURF_DECLARE_MESH

#include "Include/FluidSurfaceField.glsli"

const ivec3 NEIGHBOURS[6] = ivec3[6](ivec3(1, 0, 0), ivec3(-1, 0, 0), ivec3(0, 1, 0), ivec3(0, -1, 0), ivec3(0, 0, 1), ivec3(0, 0, -1));

void main()
{
    uint t = gl_LocalInvocationID.x;
    uint slot = dirtyList[gl_WorkGroupID.x];
    FluidSurfaceBrick brick = bricks[slot];
    uint fieldSlot = brick.fieldSlot;
    fieldBase = fieldSlot * uint(FLUID_FIELD_INTS);
    uint scratchBase = fieldSlot * uint(FLUID_CELLS);
    ivec3 brickCell = ivec3(brick.x, brick.y, brick.z) * FLUID_BRICK_CELLS;
    bool write = (brick.flags & FLUID_BRICK_LIVE) != 0u
        && (brick.extractFlags & (FLUID_BRICK_DEFERRED | FLUID_BRICK_COARSE)) == 0u
        && brick.vertices > 0u;

    if (write)
    {
        for (uint k = t; k < uint(FLUID_OWN_CELLS); k += uint(FLUID_GROUP_SIZE))
        {
            ivec3 c = fluidOwnCellOf(k);
            vec4 own = scratch[scratchBase + fluidCellIndex(c)];
            if (own.w < 0.5)
                continue;
            uint v = uint(own.w + 0.5) - 1u;
            vec3 sum = vec3(0.0);
            float n = 0.0;
            for (int i = 0; i < 6; i++)
            {
                vec4 other = scratch[scratchBase + fluidCellIndex(c + NEIGHBOURS[i])];
                if (other.w != 0.0)
                {
                    sum += other.xyz;
                    n += 1.0;
                }
            }
            vec3 grid = n > 0.0 ? mix(own.xyz, sum / n, sp.smoothing) : own.xyz;
            vec3 p = grid - vec3(brickCell);
            uint global = brickChunks[slot * FLUID_CHUNK_LIST + v / FLUID_CHUNK] * FLUID_CHUNK + (v % FLUID_CHUNK);
            fieldWriteVertex(global, grid, p, fieldOutward(fieldGradient(p)), vec3(c), 1.0);
        }
    }

    barrier();
    for (uint k = t; k < uint(FLUID_FIELD_INTS); k += uint(FLUID_GROUP_SIZE))
        field[fieldBase + k] = 0;
}
