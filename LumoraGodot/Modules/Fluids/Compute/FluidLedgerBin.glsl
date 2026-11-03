// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The ledger's bins: every live particle of every system into the ledger cell it lies in, in the system's own space,
// one share per (cell, origin, substance), then paged out to the data side through the summary's bin section. The keeper
// bins at 4 Hz for its reports, every other GPU machine at 2 Hz for its reconcile; one round serves both. Five passes on
// push constant Pass, each its own compute list:
//   0  begin (one thread): a round starts only when the last one has been paged out whole
//   1  claim: each workgroup elects one particle per key, which takes a table slot (tag = the key's hash, then the key)
//   2  add: every particle adds its volume, count, speed and height to the first slot that holds its key
//   3  compact: every slot with volume becomes one ledger entry in the round
//   4  page (one workgroup): the next 192 entries of the round into the summary, every frame until it is all out
// A slot two workgroups both claimed for one key is harmless: the add pass finds the same first one for every particle
// of that key, and the other stays empty. A table too full for a key drops the whole round (it is never paged), so a
// keeper never reports a cell empty for want of room. Nothing here is read back but the summary itself.
//
// Bins carry: Cell in the ledger's format (fill of that share, at most a whole cell), Index the particle count (low 16)
// and strand particles (high 16), Tint the mean speed in mm/s (low 16) and the fill past a whole cell (high 16), Flags
// the highest particle's height in the system's space as float bits. Water pressed together holds more than a cell's
// volume in a cell; the data side hands that up the column (FluidBinRound), so no litre is lost to the clamp.
// Summary header Reserved0 bit 0 says a page of a round is in the bin section, an empty round included. -xlinka
//
// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) readonly buffer LedgerParticles { FluidParticle p[]; } pa;
layout(set = 0, binding = 14, std430) readonly buffer LedgerHeader { FluidFrameHeader h; } hdr;
layout(set = 0, binding = 16, std430) readonly buffer LedgerSolverSystems { FluidSystemRecord s[]; } systems;
layout(set = 0, binding = 20, std430) readonly buffer LedgerCounters { uint v[]; } counters;
layout(set = 0, binding = 25, std430) buffer LedgerSummary { FLUID_SUMMARY_MEMBERS } summary;
layout(set = 0, binding = 43, std430) buffer LedgerTable { uint w[]; } table;
layout(set = 0, binding = 44, std430) buffer LedgerRound { FluidLedgerEntry e[]; } round_;
layout(set = 0, binding = 45, std430) buffer LedgerState { uint v[]; } state;
layout(set = 0, binding = 46, std430) readonly buffer LedgerSystems { vec4 v[]; } lsys;
layout(push_constant, std430) uniform LedgerPush { FluidPushConstants c; } pc;

// the solver's live count (FluidKernels.glsli's FLUID_C_LIVE)
#define LEDGER_C_LIVE 16
#define LEDGER_SLOT_WORDS 8
#define LEDGER_PROBES 64
#define LEDGER_SYSTEM_VEC4S 4
#define LEDGER_MAX_SYSTEMS 16u
#define LEDGER_ROUND_FLAG 1u
#define LEDGER_PAGE 192u
// a share whose particles average more than this (mm/s) is Moving
#define LEDGER_MOVING_MM 50u
#define LEDGER_FLAG_MOVING 1
#define LEDGER_FLAG_ON_SURFACE 2

// state words
#define S_READY 0
#define S_BINNING 1
#define S_COUNT 2
#define S_OVERFLOW 3
#define S_TOTAL 4
#define S_CURSOR 5
#define S_OPEN 6
#define S_ROUNDS 7
#define S_DROPPED 8

shared uint sK0[FLUID_GROUP_SIZE];
shared uint sK1[FLUID_GROUP_SIZE];
shared uint sK2[FLUID_GROUP_SIZE];
shared uint sValid[FLUID_GROUP_SIZE];
shared uint sOpen;
shared uint sCursor;
shared uint sTotal;

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

uint ledgerOrderedBits(float f)
{
    uint b = floatBitsToUint(f);
    return (b & 0x80000000u) != 0u ? ~b : (b | 0x80000000u);
}

float ledgerFromOrderedBits(uint b)
{
    return uintBitsToFloat((b & 0x80000000u) != 0u ? (b & 0x7FFFFFFFu) : ~b);
}

// The particle's key, its height and units in its system's space; false for a dead one or a system out of range.
bool keyOf(uint i, out uint k0, out uint k1, out uint k2, out float height, out uint units, out uint sys)
{
    k0 = 0u;
    k1 = 0u;
    k2 = 0u;
    height = 0.0;
    units = 0u;
    FluidParticle q = pa.p[i];
    sys = (q.material >> FLUID_SYSTEM_SHIFT) & FLUID_BYTE_MASK;
    if ((q.phase & FLUID_PHASE_MASK) == FLUID_PHASE_DEAD || sys >= min(hdr.h.systems, LEDGER_MAX_SYSTEMS))
        return false;
    vec4 r0 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 0u];
    vec4 r1 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 1u];
    vec4 r2 = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 2u];
    vec4 prm = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 3u];
    if (!(prm.x > 0.0) || !(prm.z > 0.0))
        return false;
    vec3 x = q.position;
    vec3 l = vec3(dot(r0.xyz, x) + r0.w, dot(r1.xyz, x) + r1.w, dot(r2.xyz, x) + r2.w);
    if (any(isnan(l)) || any(isinf(l)))
        return false;
    ivec3 c = ivec3(floor(l * prm.x));
    uint origin = (q.material >> FLUID_ORIGIN_SHIFT) & FLUID_BYTE_MASK;
    uint substance = ((q.material >> FLUID_SUBSTANCE_SHIFT) & FLUID_BYTE_MASK) - min(systems.s[sys].substanceBase, (q.material >> FLUID_SUBSTANCE_SHIFT) & FLUID_BYTE_MASK);
    k0 = uint(c.x);
    k1 = uint(c.z);
    k2 = (uint(c.y) & 0xFFFFu) | (origin << 16) | ((substance & 15u) << 24) | (sys << 28);
    height = l.y;
    units = uint(clamp(round(q.volume / prm.z), 0.0, 4.0e9));
    return true;
}

void begin()
{
    if (gl_GlobalInvocationID.x != 0u)
        return;
    if (state.v[S_READY] != 0u)
    {
        state.v[S_READY] = 0u;
        state.v[S_BINNING] = 1u;
        state.v[S_COUNT] = 0u;
        state.v[S_OVERFLOW] = 0u;
    }
    else
    {
        state.v[S_BINNING] = 0u;
    }
}

void claim(uint i, uint mask)
{
    uint t = gl_LocalInvocationIndex;
    uint k0 = 0u, k1 = 0u, k2 = 0u, units = 0u, sys = 0u;
    float height = 0.0;
    bool valid = state.v[S_BINNING] != 0u && i < counters.v[LEDGER_C_LIVE] && keyOf(i, k0, k1, k2, height, units, sys);
    sK0[t] = k0;
    sK1[t] = k1;
    sK2[t] = k2;
    sValid[t] = valid ? 1u : 0u;
    barrier();
    if (!valid)
        return;
    for (uint j = 0u; j < t; j++)
    {
        if (sValid[j] != 0u && sK0[j] == k0 && sK1[j] == k1 && sK2[j] == k2)
            return;
    }
    uint h = ledgerHash(k0, k1, k2);
    uint tag = h | 1u;
    uint start = ledgerHashWord(h ^ 0x85ebca6bu) & mask;
    for (uint probe = 0u; probe < LEDGER_PROBES; probe++)
    {
        uint base = ((start + probe) & mask) * LEDGER_SLOT_WORDS;
        uint held = table.w[base];
        if (held == 0u)
        {
            held = atomicCompSwap(table.w[base], 0u, tag);
            if (held == 0u)
            {
                table.w[base + 1u] = k0;
                table.w[base + 2u] = k1;
                table.w[base + 3u] = k2;
                return;
            }
        }
        if (held != tag)
            continue;
        uint w1 = table.w[base + 1u], w2 = table.w[base + 2u], w3 = table.w[base + 3u];
        // Claimed for this key's hash: the key itself may not be written yet in this pass; when it is, it must match.
        if ((w1 == k0 && w2 == k1 && w3 == k2) || (w1 == 0u && w2 == 0u && w3 == 0u))
            return;
    }
    atomicAdd(state.v[S_OVERFLOW], 1u);
}

void add(uint i, uint mask)
{
    if (state.v[S_BINNING] == 0u || i >= counters.v[LEDGER_C_LIVE])
        return;
    uint k0, k1, k2, units, sys;
    float height;
    if (!keyOf(i, k0, k1, k2, height, units, sys))
        return;
    FluidParticle q = pa.p[i];
    uint h = ledgerHash(k0, k1, k2);
    uint tag = h | 1u;
    uint start = ledgerHashWord(h ^ 0x85ebca6bu) & mask;
    for (uint probe = 0u; probe < LEDGER_PROBES; probe++)
    {
        uint base = ((start + probe) & mask) * LEDGER_SLOT_WORDS;
        uint held = table.w[base];
        if (held == 0u)
            break;
        if (held != tag || table.w[base + 1u] != k0 || table.w[base + 2u] != k1 || table.w[base + 3u] != k2)
            continue;
        atomicAdd(table.w[base + 4u], units);
        bool strand = (q.phase & FLUID_PHASE_MASK) == FLUID_PHASE_STRAND;
        atomicAdd(table.w[base + 5u], strand ? 0x10001u : 1u);
        atomicAdd(table.w[base + 6u], uint(min(length(q.velocity) * 1000.0, 1.0e6)));
        atomicMax(table.w[base + 7u], ledgerOrderedBits(height));
        return;
    }
    atomicAdd(state.v[S_OVERFLOW], 1u);
}

void compact(uint s, uint slots, uint cap)
{
    if (state.v[S_BINNING] == 0u || s >= slots)
        return;
    uint base = s * LEDGER_SLOT_WORDS;
    uint units = table.w[base + 4u];
    if (table.w[base] == 0u || units == 0u)
        return;
    uint k2 = table.w[base + 3u];
    uint sys = k2 >> 28;
    vec4 prm = lsys.v[sys * LEDGER_SYSTEM_VEC4S + 3u];
    uint full = uint(clamp(round(float(units) * prm.w), 0.0, 131070.0));
    uint fill = min(full, 65535u);
    uint over = full - fill;
    if (fill == 0u)
        return;
    uint at = atomicAdd(state.v[S_COUNT], 1u);
    if (at >= cap)
    {
        atomicAdd(state.v[S_OVERFLOW], 1u);
        return;
    }
    uint counts = table.w[base + 5u];
    uint count = max(counts & 0xFFFFu, 1u);
    uint speed = table.w[base + 6u] / count;
    int iy = int(k2 << 16) >> 16;
    uint origin = (k2 >> 16) & 255u;
    uint substance = (k2 >> 24) & 15u;
    int flags = speed > LEDGER_MOVING_MM ? LEDGER_FLAG_MOVING : LEDGER_FLAG_ON_SURFACE;
    FluidLedgerEntry e;
    e.system = sys;
    e.index = counts;
    e.tint = min(speed, 65535u) | (over << 16);
    e.flags = floatBitsToUint(ledgerFromOrderedBits(table.w[base + 7u]));
    e.cell = ivec4(int(table.w[base + 1u]), int(table.w[base + 2u]), (iy << 16) | int(fill), int(origin | (substance << 8)) | (flags << 12));
    round_.e[at] = e;
}

void page(uint cap)
{
    uint t = gl_LocalInvocationIndex;
    if (t == 0u)
    {
        if (state.v[S_BINNING] != 0u)
        {
            state.v[S_BINNING] = 0u;
            bool whole = state.v[S_OVERFLOW] == 0u && state.v[S_COUNT] <= cap;
            state.v[S_TOTAL] = whole ? state.v[S_COUNT] : 0u;
            state.v[S_CURSOR] = 0u;
            state.v[S_OPEN] = whole ? 1u : 0u;
            state.v[S_ROUNDS] += 1u;
            if (!whole)
            {
                state.v[S_DROPPED] += 1u;
                state.v[S_READY] = 1u;
            }
        }
        sOpen = state.v[S_OPEN];
        sCursor = state.v[S_CURSOR];
        sTotal = state.v[S_TOTAL];
    }
    barrier();
    if (sOpen == 0u)
    {
        if (t == 0u)
        {
            summary.header.bins = 0u;
            summary.header.binCursor = 0u;
            summary.header.binTotal = 0u;
            summary.header.reserved0 &= ~LEDGER_ROUND_FLAG;
        }
        return;
    }
    uint n = min(LEDGER_PAGE, sTotal - min(sCursor, sTotal));
    for (uint k = t; k < n; k += FLUID_GROUP_SIZE)
        summary.bins[k] = round_.e[sCursor + k];
    if (t == 0u)
    {
        summary.header.bins = n;
        summary.header.binCursor = sCursor;
        summary.header.binTotal = sTotal;
        summary.header.reserved0 |= LEDGER_ROUND_FLAG;
        uint next = sCursor + n;
        state.v[S_CURSOR] = next;
        if (next >= sTotal)
        {
            state.v[S_OPEN] = 0u;
            state.v[S_READY] = 1u;
        }
    }
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    uint mask = pc.c.arg;
    if (pc.c.pass == 0u)
        begin();
    else if (pc.c.pass == 1u)
        claim(i, mask);
    else if (pc.c.pass == 2u)
        add(i, mask);
    else if (pc.c.pass == 3u)
        compact(i, mask + 1u, pc.c.substep);
    else
        page(pc.c.substep);
}
