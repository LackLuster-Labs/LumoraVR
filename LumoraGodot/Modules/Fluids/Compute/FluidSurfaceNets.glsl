// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Surface nets (Gibson 1998), one workgroup per dirty brick, dispatched indirectly from the count the splat wrote.
// A brick owns the edges that start on its own nodes 0..7, so every quad is made by exactly one brick; the cells
// around those edges run from -1 to 7 and their vertices are this brick's own copies, the same bits as the
// neighbour's because the field under them is. The workgroup counts its quads and the cells they reach, numbers
// both in a fixed order, takes the chunks it needs and writes vertices, then whole quads. More than its share of
// chunks, or a pool nearly spent, and it goes again at twice the cell size. With smoothing on the vertices go to the
// scratch for FluidSurfaceSmooth instead, every cell -2..8 in grid-wide cells, so the seam vertices smooth the
// same in both bricks.
// The last reader of a field slot zeroes it, so the splat always starts on zeros. -xlinka

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
FLUID_SURF_DECLARE_TABLE
FLUID_SURF_DECLARE_BRICKS
FLUID_SURF_DECLARE_BRICK_CHUNKS
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_STACKS
FLUID_SURF_DECLARE_DIRTY
FLUID_SURF_DECLARE_FIELD
FLUID_SURF_DECLARE_SCRATCH
FLUID_SURF_DECLARE_MESH

#include "Include/FluidSurfaceField.glsli"

#define MAX_CHUNKS FLUID_CHUNK_LIST

shared uint sRef[23];
shared uint sVid[FLUID_OWN_CELLS];
shared uint sScan[FLUID_GROUP_SIZE];
shared uint sChunk[MAX_CHUNKS];
shared uint sQuads;
shared uint sVerts;
shared uint sNeed;
shared uint sCoarse;
shared uint sRetry;
shared uint sOk;

const ivec2 EDGES[12] = ivec2[12](
    ivec2(0, 1), ivec2(2, 3), ivec2(4, 5), ivec2(6, 7),
    ivec2(0, 2), ivec2(1, 3), ivec2(4, 6), ivec2(5, 7),
    ivec2(0, 4), ivec2(1, 5), ivec2(2, 6), ivec2(3, 7));

ivec3 axisVector(int a)
{
    return ivec3(a == 0 ? 1 : 0, a == 1 ? 1 : 0, a == 2 ? 1 : 0);
}

ivec3 cornerOffset(int k)
{
    return ivec3(k & 1, (k >> 1) & 1, k >> 2);
}

// The surface-nets point of a cell (step = 1 fine, 2 coarse) as its fraction across the cell; false when no edge
// crosses.
bool cellVertex(ivec3 cell, int step, out vec3 frac)
{
    float f[8];
    for (int k = 0; k < 8; k++)
        f[k] = fieldNode((cell + cornerOffset(k)) * step);
    vec3 sum = vec3(0.0);
    int n = 0;
    for (int e = 0; e < 12; e++)
    {
        float a = f[EDGES[e].x];
        float b = f[EDGES[e].y];
        if ((a > sp.iso) == (b > sp.iso))
            continue;
        float t = (sp.iso - a) / (b - a);
        sum += mix(vec3(cornerOffset(EDGES[e].x)), vec3(cornerOffset(EDGES[e].y)), t);
        n++;
    }
    frac = sum / float(max(n, 1));
    return n > 0;
}

void markCell(ivec3 c)
{
    uint i = fluidOwnCellIndex(c);
    atomicOr(sRef[i >> 5], 1u << (i & 31u));
}

bool refBit(uint i)
{
    return (sRef[i >> 5] & (1u << (i & 31u))) != 0u;
}

// Exclusive scan of sScan by thread 0; returns the total.
uint scanTotal()
{
    uint run = 0u;
    for (uint k = 0u; k < uint(FLUID_GROUP_SIZE); k++)
    {
        uint v = sScan[k];
        sScan[k] = run;
        run += v;
    }
    return run;
}

uint chunkVertex(uint local)
{
    return sChunk[local / FLUID_CHUNK] * FLUID_CHUNK + (local % FLUID_CHUNK);
}

void main()
{
    uint t = gl_LocalInvocationID.x;
    uint slot = dirtyList[gl_WorkGroupID.x];
    FluidSurfaceBrick brick = bricks[slot];
    uint fieldSlot = brick.fieldSlot;
    fieldBase = fieldSlot * uint(FLUID_FIELD_INTS);
    bool smoothing = (sp.flags & FLUID_SURF_SMOOTH) != 0u;
    bool wide = (sp.flags & FLUID_SURF_INDEX32) != 0u;
    ivec3 brickCell = ivec3(brick.x, brick.y, brick.z) * FLUID_BRICK_CELLS;

    if (t == 0u)
        sCoarse = (sp.flags & FLUID_SURF_FORCE_COARSE) != 0u ? 1u : 0u;
    barrier();

    uint quadBase = 0u;
    for (int attempt = 0; attempt < 2; attempt++)
    {
        int step = sCoarse != 0u ? 2 : 1;
        int span = FLUID_BRICK_CELLS / step;
        for (uint w = t; w < 23u; w += uint(FLUID_GROUP_SIZE))
            sRef[w] = 0u;
        barrier();

        uint myQuads = 0u;
        for (int o = int(t); o < span * span * span; o += FLUID_GROUP_SIZE)
        {
            ivec3 m = ivec3(o % span, (o / span) % span, o / (span * span));
            bool in0 = fieldNode(m * step) > sp.iso;
            for (int a = 0; a < 3; a++)
            {
                ivec3 ea = axisVector(a);
                ivec3 eb = axisVector((a + 1) % 3);
                ivec3 ec = axisVector((a + 2) % 3);
                if ((fieldNode((m + ea) * step) > sp.iso) == in0)
                    continue;
                myQuads++;
                markCell(m - eb - ec);
                markCell(m - ec);
                markCell(m);
                markCell(m - eb);
            }
        }
        sScan[t] = myQuads;
        barrier();
        if (t == 0u)
            sQuads = scanTotal();
        barrier();
        quadBase = sScan[t];
        barrier();

        uint first = t * 12u;
        uint last = min(first + 12u, uint(FLUID_OWN_CELLS));
        uint myVerts = 0u;
        for (uint k = first; k < last; k++)
            myVerts += refBit(k) ? 1u : 0u;
        sScan[t] = myVerts;
        barrier();
        if (t == 0u)
            sVerts = scanTotal();
        barrier();
        uint vid = sScan[t];
        for (uint k = first; k < last; k++)
            sVid[k] = refBit(k) ? vid++ : FLUID_NONE;
        barrier();

        if (t == 0u)
        {
            uint need = (max(sVerts, sQuads) + FLUID_CHUNK - 1u) / FLUID_CHUNK;
            sNeed = need;
            // Only a brick's own layout can lower its detail; pool pressure grows capacity instead. -xlinka
            sRetry = need > min(sp.chunksPerBrick, MAX_CHUNKS) && sCoarse == 0u ? 1u : 0u;
            if (sRetry != 0u)
                sCoarse = 1u;
        }
        barrier();
        if (sRetry == 0u)
            break;
        barrier();
    }

    if (t == 0u)
    {
        uint need = sNeed;
        sOk = 1u;
        if (need > min(sp.chunksPerBrick, MAX_CHUNKS))
        {
            sOk = 0u;
            atomicAdd(counters[FLUID_SC_CHUNK_FAIL], 1u);
        }
        else if (need > 0u)
        {
            uint available = counters[FLUID_SC_CHUNK_TOP];
            uint pop = atomicAdd(counters[FLUID_SC_CHUNK_POPS], need);
            if (pop + need <= available)
            {
                for (uint k = 0u; k < need; k++)
                    sChunk[k] = stacks[sp.brickCapacity + available - pop - need + k];
                atomicAdd(counters[FLUID_SC_CHUNK_TAKEN], need);
            }
            else
            {
                sOk = 0u;
                atomicAdd(counters[FLUID_SC_CHUNK_FAIL], 1u);
            }
        }
    }
    barrier();

    bool ok = sOk != 0u;
    int step = sCoarse != 0u ? 2 : 1;
    int span = FLUID_BRICK_CELLS / step;

    if (ok)
    {
        if (smoothing && step == 1)
        {
            uint scratchBase = fieldSlot * uint(FLUID_CELLS);
            for (uint k = t; k < uint(FLUID_CELLS); k += uint(FLUID_GROUP_SIZE))
            {
                ivec3 c = ivec3(int(k % 11u) - 2, int((k / 11u) % 11u) - 2, int(k / 121u) - 2);
                vec3 frac;
                bool has = cellVertex(c, 1, frac);
                vec3 p = vec3(brickCell + c) + frac;
                float tag = 0.0;
                if (has)
                {
                    tag = -1.0;
                    if (all(greaterThanEqual(c, ivec3(-1))) && all(lessThanEqual(c, ivec3(7))))
                    {
                        uint v = sVid[fluidOwnCellIndex(c)];
                        if (v != FLUID_NONE)
                            tag = float(v + 1u);
                    }
                }
                scratch[scratchBase + k] = vec4(p, tag);
            }
        }
        else
        {
            for (uint k = t; k < uint(FLUID_OWN_CELLS); k += uint(FLUID_GROUP_SIZE))
            {
                uint v = sVid[k];
                if (v == FLUID_NONE)
                    continue;
                ivec3 c = fluidOwnCellOf(k);
                vec3 frac;
                cellVertex(c, step, frac);
                vec3 p = vec3(c * step) + frac * float(step);
                vec3 grid = vec3(brickCell + c * step) + frac * float(step);
                vec3 g = step == 1 ? fieldGradient(p) : fieldGradientCoarse(c, p);
                fieldWriteVertex(chunkVertex(v), grid, p, fieldOutward(g), vec3(c * step), float(step));
            }
        }

        uint q = quadBase;
        for (int o = int(t); o < span * span * span; o += FLUID_GROUP_SIZE)
        {
            ivec3 m = ivec3(o % span, (o / span) % span, o / (span * span));
            bool in0 = fieldNode(m * step) > sp.iso;
            for (int a = 0; a < 3; a++)
            {
                ivec3 ea = axisVector(a);
                ivec3 eb = axisVector((a + 1) % 3);
                ivec3 ec = axisVector((a + 2) % 3);
                if ((fieldNode((m + ea) * step) > sp.iso) == in0)
                    continue;
                uint c0 = chunkVertex(sVid[fluidOwnCellIndex(m - eb - ec)]);
                uint c1 = chunkVertex(sVid[fluidOwnCellIndex(m - ec)]);
                uint c2 = chunkVertex(sVid[fluidOwnCellIndex(m)]);
                uint c3 = chunkVertex(sVid[fluidOwnCellIndex(m - eb)]);
                uint qs = chunkVertex(q);
                // front faces wind clockwise: seen from outside, where the field drops below the iso level
                if (in0)
                    FLUID_WRITE_QUAD(ib, wide, qs, c0, c3, c2, c1)
                else
                    FLUID_WRITE_QUAD(ib, wide, qs, c0, c1, c2, c3)
                q++;
            }
        }
    }

    if (t == 0u)
    {
        uint need = ok ? sNeed : 0u;
        for (uint k = 0u; k < need; k++)
            brickChunks[slot * FLUID_CHUNK_LIST + k] = sChunk[k];
        uint flags = FLUID_BRICK_LIVE | (sCoarse != 0u ? FLUID_BRICK_COARSE : 0u) | (ok ? 0u : FLUID_BRICK_DEFERRED);
        bricks[slot].chunkCount = need;
        bricks[slot].vertices = ok ? sVerts : 0u;
        bricks[slot].quads = ok ? sQuads : 0u;
        bricks[slot].extractFlags = flags;
        if (brick.particles == 0u)
        {
            // nothing reaches this brick any more: the slot goes back, the key stays vacant
            table[brick.tableEntry].y = FLUID_NONE;
            bricks[slot].flags = 0u;
            uint top = atomicAdd(counters[FLUID_SC_BRICK_TOP], 1u);
            stacks[top] = slot;
        }
        else
        {
            bricks[slot].flags = flags;
        }
        atomicAdd(counters[FLUID_SC_EXTRACTED], 1u);
        if (ok)
        {
            atomicAdd(counters[FLUID_SC_QUADS], sQuads);
            atomicAdd(counters[FLUID_SC_VERTS], sVerts);
        }
        if (sCoarse != 0u)
            atomicAdd(counters[FLUID_SC_COARSE], 1u);
    }

    if (!smoothing)
    {
        barrier();
        for (uint k = t; k < uint(FLUID_FIELD_INTS); k += uint(FLUID_GROUP_SIZE))
            field[fieldBase + k] = 0;
    }
}
