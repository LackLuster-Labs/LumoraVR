// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The reconcile's hands where a cell holds too much here: particles in a cell the data side marked go, the top of the
// cell first. The action table holds one slot per acted cell (its key: x, z, then y in the low 16 bits
// and the system in the top 4), the share of the cell's volume to take, and the middle and half height of the fluid in
// it; a particle's chance runs from half the share at the bottom to one and a half times it at the top, so the cell
// loses its share on average, and a share of all of it takes everything. The pick is
// seeded by the particle's id and the action round, so it is the same on every run.
//
// A chosen particle dies the way frame end A kills one (dead and dying: the next commands pass lets go of its bonds,
// the sort frees its id), rather than fading: a fading particle that falls under the merge volume gives what is left
// of it to a neighbour, so a fade only ever took about 60% of what it was asked to. FluidReseed.glsl reads the same
// table for the cells that hold too little. -xlinka
//
// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) buffer LedgerParticles { FluidParticle p[]; } pa;
layout(set = 0, binding = 2, std430) readonly buffer LedgerIds { uint v[]; } idA;
layout(set = 0, binding = 14, std430) readonly buffer LedgerHeader { FluidFrameHeader h; } hdr;
layout(set = 0, binding = 20, std430) buffer LedgerCounters { uint v[]; } counters;
layout(set = 0, binding = 45, std430) buffer LedgerState { uint v[]; } state;
layout(set = 0, binding = 46, std430) readonly buffer LedgerSystems { vec4 v[]; } lsys;
layout(set = 0, binding = 47, std430) readonly buffer LedgerActions { uint w[]; } actions;
layout(push_constant, std430) uniform LedgerPush { FluidPushConstants c; } pc;

#define LEDGER_C_LIVE 16
#define LEDGER_SLOT_WORDS 8
#define LEDGER_PROBES 64
#define LEDGER_SYSTEM_VEC4S 4
#define LEDGER_MAX_SYSTEMS 16u
#define LEDGER_ACTION_TRIM 0u
// FluidKernels.glsli's refined flag and the refinement pool's counter: a refined particle that dies hands its place back
#define LEDGER_PARTICLE_REFINED 0x100000u
#define LEDGER_PARTICLE_DYING 0x200000u
#define LEDGER_C_POOL 22
#define S_FADED 9

uint ledgerHashWord(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

uint ledgerHash(uint k0, uint k1, uint k2)
{
    return ledgerHashWord(k0 ^ ledgerHashWord(k1 ^ ledgerHashWord(k2 + 0x9e3779b9u)));
}

float ledgerRandom(uint a, uint b)
{
    return float(ledgerHashWord(a ^ ledgerHashWord(b + 0x9e3779b9u)) >> 8) * (1.0 / 16777216.0);
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= counters.v[LEDGER_C_LIVE])
        return;
    FluidParticle q = pa.p[i];
    uint sys = (q.material >> FLUID_SYSTEM_SHIFT) & FLUID_BYTE_MASK;
    if ((q.phase & FLUID_PHASE_MASK) == FLUID_PHASE_DEAD || (q.phase & FLUID_PARTICLE_FADING) != 0u
        || sys >= min(hdr.h.systems, LEDGER_MAX_SYSTEMS))
        return;
    vec4 r0 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 0u];
    vec4 r1 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 1u];
    vec4 r2 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 2u];
    vec4 prm = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 3u];
    if (!(prm.x > 0.0))
        return;
    vec3 x = q.position;
    vec3 l = vec3(dot(r0.xyz, x) + r0.w, dot(r1.xyz, x) + r1.w, dot(r2.xyz, x) + r2.w);
    ivec3 c = ivec3(floor(l * prm.x));
    uint k0 = uint(c.x);
    uint k1 = uint(c.z);
    uint k2 = (uint(c.y) & 0xFFFFu) | (sys << 28);
    uint mask = pc.c.arg;
    uint h = ledgerHash(k0, k1, k2);
    uint tag = h | 1u;
    uint start = ledgerHashWord(h ^ 0x85ebca6bu) & mask;
    for (uint probe = 0u; probe < LEDGER_PROBES; probe++)
    {
        uint base = ((start + probe) & mask) * LEDGER_SLOT_WORDS;
        uint held = actions.w[base];
        if (held == 0u)
            return;
        if (held != tag || actions.w[base + 1u] != k0 || actions.w[base + 2u] != k1 || actions.w[base + 3u] != k2)
            continue;
        if (actions.w[base + 4u] != LEDGER_ACTION_TRIM)
            return;
        float share = uintBitsToFloat(actions.w[base + 5u]);
        float middle = uintBitsToFloat(actions.w[base + 6u]);
        float halfHeight = max(uintBitsToFloat(actions.w[base + 7u]), 1e-6);
        float weight = clamp(1.0 + 0.5 * (l.y - middle) / halfHeight, 0.5, 1.5);
        float chance = share >= 0.999 ? 1.0 : clamp(share * weight, 0.0, 1.0);
        if (ledgerRandom(idA.v[i], pc.c.substep) >= chance)
            return;
        if ((q.phase & LEDGER_PARTICLE_REFINED) != 0u)
            atomicAdd(counters.v[LEDGER_C_POOL], 0xFFFFFFFFu);
        pa.p[i].phase = FLUID_PHASE_DEAD | LEDGER_PARTICLE_DYING;
        atomicAdd(state.v[S_FADED], 1u);
        return;
    }
}
