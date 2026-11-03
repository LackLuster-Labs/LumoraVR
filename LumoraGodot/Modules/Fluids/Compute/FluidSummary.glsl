// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The frame's summary, one workgroup: the header, per system status (bounds, vmax, energy, counts, the guards'
// flags), and the sparse stats tables compacted into their sections in table order (contacts, limbs, impulses,
// drained litres, query answers), so the same frame always reads back the same bytes. Bakes were appended by frame
// end A; the keeper's bins belong to the ledger kernels and are left alone. One async read of this buffer is the
// module's only read-back. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_SCAN_THREADS, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_HEADER
FLUID_DECLARE_FRAMES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_SUMMARY
FLUID_DECLARE_PUSH

shared uint sScan[FLUID_SCAN_THREADS];
shared uint sFlags;
shared uint sNan;
shared uint sVmax;
shared uint sEnergy;

// a jump of this many times in a system's kinetic energy, with no command driving it, is the watchdog's trigger
#define ENERGY_RISE 10.0
#define ENERGY_FLOOR 1e-3

// This thread's place among the flagged threads of one round, and the round's count.
uint compactRank(bool flag, out uint total)
{
    uint t = gl_LocalInvocationIndex;
    uint mine = flag ? 1u : 0u;
    sScan[t] = mine;
    barrier();
    for (uint off = 1u; off < FLUID_SCAN_THREADS; off <<= 1)
    {
        uint v = t >= off ? sScan[t - off] : 0u;
        barrier();
        sScan[t] += v;
        barrier();
    }
    total = sScan[FLUID_SCAN_THREADS - 1u];
    uint rank = sScan[t] - mine;
    barrier();
    return rank;
}

void main()
{
    uint t = gl_LocalInvocationIndex;
    if (t == 0u)
    {
        sFlags = 0u;
        sNan = 0u;
        sVmax = 0u;
        sEnergy = 0u;
    }
    barrier();

    uint systemCount = min(hdr.h.systems, FLUID_MAX_SYSTEMS);
    if (t < systemCount)
    {
        uint s = FLUID_S_SYSTEM + t * FLUID_S_SYSTEM_STRIDE;
        uint live = stats.v[s + FLUID_S_SYS_LIVE];
        uint awake = stats.v[s + FLUID_S_SYS_AWAKE];
        float energy = fluidFromFixed(stats.v[s + FLUID_S_SYS_ENERGY], FLUID_ENERGY_UNIT);
        float previous = uintBitsToFloat(counters.v[FLUID_C_ENERGY_PREV + t]);
        uint nan = stats.v[s + FLUID_S_SYS_NAN];
        uint flags = 0u;
        if (awake == 0u)
            flags |= FLUID_SUMMARY_ALL_ASLEEP;
        if (live > 0u && float(nan) > 0.01 * float(live))
            flags |= FLUID_SUMMARY_NAN_RESET;
        if (hdr.h.commands == 0u && previous > 0.0 && energy > ENERGY_RISE * max(previous, ENERGY_FLOOR))
            flags |= FLUID_SUMMARY_ENERGY_RISE;
        counters.v[FLUID_C_ENERGY_PREV + t] = floatBitsToUint(energy);
        FluidSystemStatus st;
        if (live > 0u)
        {
            st.boundsMin = vec3(fluidFromOrderedBits(~stats.v[s + FLUID_S_SYS_MIN]), fluidFromOrderedBits(~stats.v[s + FLUID_S_SYS_MIN + 1u]), fluidFromOrderedBits(~stats.v[s + FLUID_S_SYS_MIN + 2u]));
            st.boundsMax = vec3(fluidFromOrderedBits(stats.v[s + FLUID_S_SYS_MAX]), fluidFromOrderedBits(stats.v[s + FLUID_S_SYS_MAX + 1u]), fluidFromOrderedBits(stats.v[s + FLUID_S_SYS_MAX + 2u]));
        }
        else
        {
            st.boundsMin = vec3(0.0);
            st.boundsMax = vec3(0.0);
        }
        st.vmax = uintBitsToFloat(stats.v[s + FLUID_S_SYS_VMAX]);
        st.kineticEnergy = energy;
        st.awake = awake;
        st.live = live;
        st.nanCount = nan;
        st.flags = flags;
        st.strands = stats.v[s + FLUID_S_SYS_STRANDS];
        st.anchors = stats.v[s + FLUID_S_SYS_ANCHORS];
        st.overflow = 0u;
        st.snaps = stats.v[s + FLUID_S_SYS_SNAPS];
        summary.systems[t] = st;
        atomicOr(sFlags, flags & ~FLUID_SUMMARY_ALL_ASLEEP);
        atomicAdd(sNan, nan);
        atomicMax(sVmax, stats.v[s + FLUID_S_SYS_VMAX]);
        atomicAdd(sEnergy, stats.v[s + FLUID_S_SYS_ENERGY]);
    }

    // contacts: person x part
    uint people_ = min(hdr.h.people, FLUID_S_PEOPLE_MAX);
    uint total;
    uint written = 0u;
    for (uint base = 0u; base < people_ * uint(FLUID_BODY_PARTS); base += FLUID_SCAN_THREADS)
    {
        uint e = base + t;
        uint s = FLUID_S_PARTS + e * 4u;
        bool hit = e < people_ * uint(FLUID_BODY_PARTS) && stats.v[s] > 0u;
        uint rank = compactRank(hit, total);
        if (hit && written + rank < uint(FLUID_SUMMARY_CONTACTS_CAPACITY))
        {
            FluidContactRecord c;
            c.person = e / uint(FLUID_BODY_PARTS);
            c.part = (e % uint(FLUID_BODY_PARTS)) | ((stats.v[s + 3u] & 255u) << 8);
            c.depth = uintBitsToFloat(stats.v[s + 1u]);
            c.speed = uintBitsToFloat(stats.v[s + 2u]);
            summary.contacts[written + rank] = c;
        }
        written += total;
    }
    uint contacts = min(written, uint(FLUID_SUMMARY_CONTACTS_CAPACITY));

    // limbs: person x limb
    uint limbsTotal = 0u;
    {
        uint e = t;
        uint s = FLUID_S_LIMBS + e * 4u;
        bool hit = e < people_ * uint(FLUID_LIMBS) && (stats.v[s] != 0u || stats.v[s + 1u] != 0u || stats.v[s + 2u] != 0u);
        uint rank = compactRank(hit, limbsTotal);
        if (hit && rank < uint(FLUID_SUMMARY_LIMBS_CAPACITY))
        {
            FluidLimbRecord l;
            l.person = e / uint(FLUID_LIMBS);
            l.limb = e % uint(FLUID_LIMBS);
            l.stuck = fluidFromFixed(stats.v[s], FLUID_STUCK_UNIT);
            l.strands = stats.v[s + 1u];
            l.snaps = stats.v[s + 2u];
            l.lastSnapLength = uintBitsToFloat(stats.v[s + 3u]);
            l.pull = 0.0;
            l.reserved0 = 0u;
            summary.limbs[rank] = l;
        }
    }

    // impulses: rigid bodies
    uint impulsesTotal = 0u;
    {
        uint s = FLUID_S_IMPULSES + t * 8u;
        bool hit = stats.v[s + 6u] > 0u;
        uint rank = compactRank(hit, impulsesTotal);
        if (hit && rank < uint(FLUID_SUMMARY_IMPULSES_CAPACITY))
        {
            FluidImpulseRecord r;
            r.linear = vec3(fluidFromFixed(stats.v[s], FLUID_IMPULSE_UNIT), fluidFromFixed(stats.v[s + 1u], FLUID_IMPULSE_UNIT), fluidFromFixed(stats.v[s + 2u], FLUID_IMPULSE_UNIT));
            r.body = t;
            r.angular = vec3(fluidFromFixed(stats.v[s + 3u], FLUID_IMPULSE_UNIT), fluidFromFixed(stats.v[s + 4u], FLUID_IMPULSE_UNIT), fluidFromFixed(stats.v[s + 5u], FLUID_IMPULSE_UNIT));
            r.count = stats.v[s + 6u];
            summary.impulses[rank] = r;
        }
    }

    // Volumes: each drain's litres removed this frame (kind 0, Index = the drain's index, from Remove's B.y - 1), then
    // each container's litres now (kind 1, Index = its frame record's container index, Reserved0).
    written = 0u;
    {
        uint c = t;
        bool hit = c < 256u && stats.v[FLUID_S_DRAINS + c] > 0u;
        uint rank = compactRank(hit, total);
        if (hit && written + rank < uint(FLUID_SUMMARY_VOLUMES_CAPACITY))
        {
            FluidVolumeRecord v;
            v.kind = 0u;
            v.index = c;
            v.litres = float(stats.v[FLUID_S_DRAINS + c]) * 1e-9;
            v.system = FLUID_NONE;
            summary.volumes[written + rank] = v;
        }
        written += total;
    }
    {
        uint f = t + 1u;
        bool hit = f < min(hdr.h.frames, FLUID_SCAN_THREADS) && (frames.f[f].flags & FLUID_FRAME_RECORD_CONTAINER) != 0u
            && frames.f[f].system < systemCount;
        uint rank = compactRank(hit, total);
        if (hit && written + rank < uint(FLUID_SUMMARY_VOLUMES_CAPACITY))
        {
            uint sysIndex = frames.f[f].system;
            FluidVolumeRecord v;
            v.kind = 1u;
            v.index = frames.f[f].container;
            v.litres = float(stats.v[FLUID_S_SYSTEM + sysIndex * FLUID_S_SYSTEM_STRIDE + FLUID_S_SYS_MICROLITRES]) * 1e-6;
            v.system = sysIndex;
            summary.volumes[written + rank] = v;
        }
        written += total;
    }
    uint volumes = min(written, uint(FLUID_SUMMARY_VOLUMES_CAPACITY));

    uint queryCount = min(hdr.h.queries, uint(FLUID_SUMMARY_QUERIES_CAPACITY));
    if (t < queryCount)
    {
        FluidQueryResult q;
        q.localLitres = float(stats.v[FLUID_S_QUERIES + t * 2u]) * 1e-6;
        q.moving = float(stats.v[FLUID_S_QUERIES + t * 2u + 1u]);
        q.system = FLUID_NONE;
        q.reserved0 = 0u;
        summary.queries[t] = q;
    }
    barrier();

    if (t == 0u)
    {
        uint awake = counters.v[FLUID_C_AWAKE];
        uint flags = sFlags;
        if (awake == 0u)
            flags |= FLUID_SUMMARY_ALL_ASLEEP;
        if (counters.v[FLUID_C_POOL_EMPTY] != 0u)
            flags |= FLUID_SUMMARY_POOL_EMPTY;
        summary.header.frame = counters.v[FLUID_C_FRAME];
        // Match delayed impulses to the body table submitted with this block. -xlinka
        summary.header.submittedSequence = hdr.h.sequence;
        summary.header.flags = flags;
        summary.header.awake = awake;
        summary.header.live = counters.v[FLUID_C_LIVE];
        summary.header.contacts = contacts;
        summary.header.limbs = min(limbsTotal, uint(FLUID_SUMMARY_LIMBS_CAPACITY));
        summary.header.bakes = min(stats.v[FLUID_S_BAKES], uint(FLUID_SUMMARY_BAKES_CAPACITY));
        summary.header.pending = 0u;
        summary.header.impulses = min(impulsesTotal, uint(FLUID_SUMMARY_IMPULSES_CAPACITY));
        summary.header.volumes = volumes;
        summary.header.systems = systemCount;
        summary.header.queries = queryCount;
        summary.header.props = 0u;
        summary.header.vmax = uintBitsToFloat(sVmax);
        summary.header.kineticEnergy = fluidFromFixed(sEnergy, FLUID_ENERGY_UNIT);
        summary.header.nanCount = sNan;
        summary.header.overflow = counters.v[FLUID_C_SPAWN_OVERFLOW];
        summary.header.neighbourOverflow = counters.v[FLUID_C_NEIGHBOUR_OVERFLOW];
        summary.header.poolEmpty = counters.v[FLUID_C_POOL_EMPTY];
        counters.v[FLUID_C_NEIGHBOUR_OVERFLOW] = 0u;
        counters.v[FLUID_C_SPAWN_OVERFLOW] = 0u;
    }
}
