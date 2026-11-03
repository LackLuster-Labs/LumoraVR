// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Refilling from the ledger, on the GPU side. A cell that holds fluid here but less than the ledger says (and at least
// half of it) has its particles grow toward the ledger's amount, at most half again in one round: the same volume and
// mass change a merge makes, so the density constraint spreads it. The data side reads the cell's growth factor into
// the reconcile's action table (FluidLedgerReconcile.glsl takes the cells that hold too much from the same table).
// Cells with few or no particles here, a late joiner's whole puddle and the keeper's reseed of what the host wrote on
// its own are laid in by the solver's commands kernel instead, one Reseed command per cell, nearest first, at most
// 2048 particles a frame (FluidReseedPlan). -xlinka
//
// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) buffer LedgerParticles { FluidParticle p[]; } pa;
layout(set = 0, binding = 14, std430) readonly buffer LedgerHeader { FluidFrameHeader h; } hdr;
layout(set = 0, binding = 20, std430) readonly buffer LedgerCounters { uint v[]; } counters;
layout(set = 0, binding = 45, std430) buffer LedgerState { uint v[]; } state;
layout(set = 0, binding = 46, std430) readonly buffer LedgerSystems { vec4 v[]; } lsys;
layout(set = 0, binding = 47, std430) readonly buffer LedgerActions { uint w[]; } actions;
layout(push_constant, std430) uniform LedgerPush { FluidPushConstants c; } pc;

#define LEDGER_C_LIVE 16
#define LEDGER_SLOT_WORDS 8
#define LEDGER_PROBES 64
#define LEDGER_SYSTEM_VEC4S 4
#define LEDGER_MAX_SYSTEMS 16u
#define LEDGER_ACTION_GROW 1u
#define LEDGER_MAX_GROWTH 1.5
#define S_GROWN 10

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

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= counters.v[LEDGER_C_LIVE])
        return;
    FluidParticle q = pa.p[i];
    uint sys = (q.material >> FLUID_SYSTEM_SHIFT) & FLUID_BYTE_MASK;
    if ((q.phase & FLUID_PHASE_MASK) != FLUID_PHASE_BULK || (q.phase & FLUID_PARTICLE_FADING) != 0u
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
        if (actions.w[base + 4u] != LEDGER_ACTION_GROW)
            return;
        float growth = clamp(uintBitsToFloat(actions.w[base + 5u]), 1.0, LEDGER_MAX_GROWTH);
        q.volume *= growth;
        q.mass *= growth;
        q.phase &= ~FLUID_PARTICLE_ASLEEP;
        pa.p[i] = q;
        atomicAdd(state.v[S_GROWN], 1u);
        return;
    }
}
