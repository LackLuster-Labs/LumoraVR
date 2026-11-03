// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Skins every contact vertex of the people near fluid, at the current and the last pose, the way the drawn avatar
// is skinned: rest position plus the blendshape deltas at the current weights, then four bone weights. Shares the
// first compute list with the commands kernel. A vertex finds its person by range (at most 32), then its deltas by
// binary search: each person's deltas are sorted by vertex index (FluidBlendDelta's low 20 bits, counted in the
// frame's contact vertex array). The contact mesh goes up once per equip but the bone list is rebuilt every frame,
// so a vertex names its bones relative to its person (low 16 bits): the part bones first, from BoneStart, then the
// skin bones from SkinBoneStart. Bits 16-23 of the first bone word carry the vertex's body part, which the skinned
// vertex keeps as its region. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_HEADER
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_BONES
FLUID_DECLARE_CONTACT_VERTS
FLUID_DECLARE_BLENDS
FLUID_DECLARE_BLEND_WEIGHTS
FLUID_DECLARE_SKINNED
FLUID_DECLARE_PUSH

#define VERTEX_MASK 0xFFFFFu

void main()
{
    uint k = gl_GlobalInvocationID.x;
    if (k >= hdr.h.contactVertices)
        return;
    FluidContactVertex V = contactVerts.v[k];
    vec3 rest = V.position;
    vec3 restNormal = fluidUnpackNormal(V.normal);

    uint people_ = min(hdr.h.people, FLUID_S_PEOPLE_MAX);
    uint boneStart = 0u;
    uint boneCount = 0u;
    uint skinStart = 0u;
    uint skinCount = 0u;
    for (uint p = 0u; p < people_; p++)
    {
        FluidPersonRecord P = people.p[p];
        if (k < P.vertexStart || k >= P.vertexStart + P.vertexCount)
            continue;
        boneStart = P.boneStart;
        boneCount = P.boneCount;
        skinStart = P.skinBoneStart;
        skinCount = P.skinBoneCount;
        if (P.blendCount == 0u)
            break;
        uint lo = P.blendStart;
        uint hi = P.blendStart + P.blendCount;
        while (lo < hi)
        {
            uint mid = (lo + hi) / 2u;
            if ((blends.d[mid].vertexShape & VERTEX_MASK) < k)
                lo = mid + 1u;
            else
                hi = mid;
        }
        for (uint m = lo; m < P.blendStart + P.blendCount; m++)
        {
            FluidBlendDelta D = blends.d[m];
            if ((D.vertexShape & VERTEX_MASK) != k)
                break;
            float w = blendWeights.v[D.vertexShape >> 20];
            if (w > 0.0)
                rest += D.delta * w;
        }
        break;
    }

    uint boneIndex[4] = uint[4](V.bone0, V.bone1, V.bone2, V.bone3);
    vec3 now = vec3(0.0);
    vec3 last = vec3(0.0);
    vec3 normal = vec3(0.0);
    float total = 0.0;
    for (uint b = 0u; b < 4u; b++)
    {
        float w = V.weights[b];
        uint local = boneIndex[b] & FLUID_CONTACT_BONE_MASK;
        bool skin = local >= FLUID_SKIN_BONE_FIRST;
        uint bi = skin ? skinStart + local - FLUID_SKIN_BONE_FIRST : boneStart + local;
        if (w <= 0.0 || (skin ? local - FLUID_SKIN_BONE_FIRST >= skinCount : local >= boneCount) || bi >= hdr.h.bones)
            continue;
        FluidBoneRecord B = bones.b[bi];
        now += w * fluidAffine(B.row0, B.row1, B.row2, rest);
        last += w * fluidAffine(B.lastRow0, B.lastRow1, B.lastRow2, rest);
        normal += w * fluidAffineVector(B.row0, B.row1, B.row2, restNormal);
        total += w;
    }
    if (total <= 0.0)
    {
        now = rest;
        last = rest;
        normal = restNormal;
    }
    else
    {
        now /= total;
        last /= total;
    }
    skinned.v[k].position = now;
    skinned.v[k].normal = fluidPackNormal(length(normal) > 1e-12 ? normalize(normal) : vec3(0.0, 1.0, 0.0));
    skinned.v[k].lastPosition = last;
    skinned.v[k].region = (V.bone0 >> FLUID_CONTACT_PART_SHIFT) & 0xFFu;
}
