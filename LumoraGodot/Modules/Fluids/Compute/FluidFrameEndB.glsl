// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Frame end B, one thread per awake slot: applies the nL volume moves, commits the label round, makes the bonds two
// particles both proposed, merges each ligament into a parallel one nearby (from A's list), picks each strand's worst
// link (an atomicMax of fluidSnapKey on the strand's label), decides refinement (a link is split only when both of
// its ends hold its claim, so a particle splits at most one link a frame). Refine candidates are counted per
// 64-slot chunk for the scan in C. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"
#include "Include/FluidThinning.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_IDA
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_BONDS
FLUID_DECLARE_ANCHORS
FLUID_DECLARE_ACC
FLUID_DECLARE_AUX
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_BY_ID
FLUID_DECLARE_CANDIDATES
FLUID_DECLARE_PUSH

shared uint sRefines;

#define REFINE_LENGTH 1.5
// Only overlapping parallel liquid coalesces; nearby separate contacts still carry their own bridge. -xlinka
#define MERGE_COS 0.7

float linkBadness(float radius, uint strainFlags, FluidSubstanceRecord sub)
{
    float s = sub.minThread / max(radius, 1e-9);
    if (sub.stretchLimit > 0.0)
        s = max(s, fluidStrainOf(strainFlags) / sub.stretchLimit);
    return s;
}

vec3 ligamentEnd(uint e, uint end)
{
    uint o = e + 2u + 3u * end;
    return vec3(uintBitsToFloat(candidates.v[o]), uintBitsToFloat(candidates.v[o + 1u]), uintBitsToFloat(candidates.v[o + 2u]));
}

bool sameFreeEnd(uint a, uint b)
{
    return a == b || ((anchors.a[a].kindAge | anchors.a[b].kindAge) & FLUID_ANCHOR_KIND_MASK) == FLUID_ANCHOR_NONE;
}

float ligamentRadius(uint u, uint v)
{
    for (uint k = 0u; k < hdr.h.bondSlots; k++)
    {
        if (bonds.b[u * hdr.h.bondSlots + k].partner == v)
            return bonds.b[u * hdr.h.bondSlots + k].radius;
        if (bonds.b[v * hdr.h.bondSlots + k].partner == u)
            return bonds.b[v * hdr.h.bondSlots + k].radius;
    }
    return 0.0;
}

// Coalesce overlapping necks of the same fluid and frame, preserving every distinct held contact. -xlinka
uint ligamentMergeTarget(uint id, uint j, vec3 xi, vec3 xj, float radius, uint material)
{
    uint listed = candidates.v[FLUID_K_LIGAMENT_COUNT];
    if (listed > FLUID_K_LIGAMENT_CAP)
        return FLUID_NONE;
    vec3 axis = xj - xi;
    float axisLen = length(axis);
    if (axisLen < 1e-9)
        return FLUID_NONE;
    axis /= axisLen;
    uint bestLo = min(id, j), bestHi = max(id, j);
    bool found = false;
    uint identity = material & 0x00FFFFFFu;
    for (uint r = 0u; r < listed; r++)
    {
        uint e = FLUID_K_LIGAMENTS + r * FLUID_K_LIGAMENT_STRIDE;
        uint u = candidates.v[e], v = candidates.v[e + 1u];
        uint lo = min(u, v), hi = max(u, v);
        if (lo > bestLo || (lo == bestLo && hi >= bestHi))
            continue;
        uint us = idToSlot.v[u], vs = idToSlot.v[v];
        if (us == FLUID_NONE || vs == FLUID_NONE
            || (pa.p[us].material & 0x00FFFFFFu) != identity || (pa.p[vs].material & 0x00FFFFFFu) != identity)
            continue;
        vec3 xu = ligamentEnd(e, 0u), xv = ligamentEnd(e, 1u);
        vec3 other = xv - xu;
        float otherLen = length(other);
        if (otherLen < 1e-9)
            continue;
        float c = dot(other, axis) / otherLen;
        uint nearId = c >= 0.0 ? u : v, farId = c >= 0.0 ? v : u;
        if (!sameFreeEnd(id, nearId) || !sameFreeEnd(j, farId))
            continue;
        float otherRadius = ligamentRadius(u, v);
        if (otherRadius <= 0.0)
            continue;
        float reach2 = (radius + otherRadius) * (radius + otherRadius);
        vec3 nearEnd = c >= 0.0 ? xu : xv;
        vec3 farEnd = c >= 0.0 ? xv : xu;
        vec3 dn = nearEnd - xi, df = farEnd - xj;
        if (abs(c) <= MERGE_COS || dot(dn, dn) > reach2 || dot(df, df) > reach2)
            continue;
        bestLo = lo;
        bestHi = hi;
        found = true;
    }
    return found ? bestLo : FLUID_NONE;
}

bool proposed(uint by, uint id)
{
    uint b = by * FLUID_BY_ID_STRIDE + FLUID_ID_PROPOSALS;
    for (uint q = 0u; q < FLUID_ID_PROPOSAL_COUNT; q++)
    {
        if (byId.v[b + q] == id)
            return true;
    }
    return false;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    if (lid == 0u)
        sRefines = 0u;
    barrier();

    uint awake = counters.v[FLUID_C_AWAKE];
    bool refine = false;
    if (i < awake)
    {
        uint id = idA.v[i];
        uint b = id * FLUID_BY_ID_STRIDE;
        FluidParticle P = pa.p[i];
        // the phase frame end A decided, committed here (it left the old one for its neighbours to read)
        P.phase = floatBitsToUint(aux.v[id * FLUID_AUX_STRIDE].w);
        pa.p[i].phase = P.phase;
        uint phase = fluidPhaseOf(P.phase);
        int nl = atomicExchange(acc.a[id].volumeNl, 0);
        if (nl != 0 && phase != FLUID_PHASE_DEAD)
        {
            // the mass follows: merged and thinned particles kept their old mass, and so their old inverse mass in
            // every bond and anchor
            P.volume = max(P.volume + fluidFromNanolitres(nl), 1e-12);
            pa.p[i].volume = P.volume;
            pa.p[i].mass = P.volume * substances.s[fluidSubstanceOf(P.material)].density;
        }
        // D clears claims as it finishes; keep a stable copy for its neighbouring neck tests. -xlinka
        aux.v[id * FLUID_AUX_STRIDE + 1u].w = uintBitsToFloat(byId.v[b + FLUID_ID_REFINE_CLAIM]);
        uint label = byId.v[b + FLUID_ID_LABEL_NEXT];
        acc.a[id].label = label;
        byId.v[b + FLUID_ID_REFINE_KEY] = FLUID_NONE;

        if (phase != FLUID_PHASE_DEAD)
        {
            FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
            float d = sub.spacing;
            uint slotCount = hdr.h.bondSlots;

            // D keeps linked refinement nodes. Clear stale flags here, before D can sever either end. -xlinka
            for (uint k = 0u; k < slotCount; k++)
            {
                uint at = id * slotCount + k;
                if (bonds.b[at].partner == FLUID_NONE)
                    bonds.b[at].strainFlags = 0u;
            }

            // New bonds, mutual proposals only. Both ends measure the same distance and draw the same noise.
            for (uint q = 0u; q < FLUID_ID_PROPOSAL_COUNT; q++)
            {
                uint j = byId.v[b + FLUID_ID_PROPOSALS + q];
                if (j == FLUID_NONE || !proposed(j, id))
                    continue;
                uint js = idToSlot.v[j];
                if (js == FLUID_NONE)
                    continue;
                for (uint k = 0u; k < slotCount; k++)
                {
                    uint at = id * slotCount + k;
                    if (bonds.b[at].partner != FLUID_NONE)
                        continue;
                    bonds.b[at].partner = j;
                    // A viscous network's bond is never shorter than density lets two particles sit: made while an
                    // impact squeezed them, it pulled them together for good while density pushed them apart.
                    bonds.b[at].restLength = max(distance(P.position, pa.p[js].position), ((sub.flags & FLUID_SUBSTANCE_STIFF) != 0u ? FLUID_BOND_REST_MIN : 0.0) * d);
                    bonds.b[at].radius = sub.bondRadius * (0.95 + 0.1 * fluidRandom(min(id, j), max(id, j)));
                    bonds.b[at].strainFlags = 0u;
                    bonds.b[at].lambda = 0.0;
                    break;
                }
            }

            // The strand's worst link, and the refine decision from the claims A made.
            bool poolOpen = counters.v[FLUID_C_POOL] < pc.c.arg;
            float longest = 0.0;
            uint refineKey = FLUID_NONE;
            uint refinePartner = FLUID_NONE;
            uint myAge = acc.a[id].strandAge & FLUID_STRAND_AGE_MASK;
            for (uint k = 0u; k < slotCount; k++)
            {
                FluidBond bnd = bonds.b[id * slotCount + k];
                uint j = bnd.partner;
                if (j == FLUID_NONE || (bnd.strainFlags & (FLUID_BOND_STRAND | FLUID_BOND_OWNER)) != (FLUID_BOND_STRAND | FLUID_BOND_OWNER))
                    continue;
                uint js = idToSlot.v[j];
                uint jPhase = js < awake ? floatBitsToUint(aux.v[j * FLUID_AUX_STRIDE].w) : (js != FLUID_NONE ? pa.p[js].phase : 0u);
                if (js == FLUID_NONE || fluidPhaseOf(jPhase) == FLUID_PHASE_DEAD)
                    continue;
                if (phase != FLUID_PHASE_STRAND && fluidPhaseOf(jPhase) != FLUID_PHASE_STRAND)
                {
                    // Parallel ligaments merge their connectivity in D; particles retain their liquid. -xlinka
                    uint into = (P.material & 0x00FFFFFFu) == (pa.p[js].material & 0x00FFFFFFu)
                        ? ligamentMergeTarget(id, j, P.position, pa.p[js].position, bnd.radius, P.material) : FLUID_NONE;
                    if (into != FLUID_NONE)
                    {
                        bonds.b[id * slotCount + k].strainFlags = bnd.strainFlags | FLUID_BOND_MERGED;
                        continue;
                    }
                }
                bool slackPiece = ((P.phase | jPhase) & FLUID_PARTICLE_SLACK) != 0u;
                // Recoil ignores the old elastic failure, but an exhausted liquid neck still parts. -xlinka
                float s = slackPiece ? sub.minThread / max(bnd.radius, 1e-9) : linkBadness(bnd.radius, bnd.strainFlags, sub);
                if (s >= 1.0)
                {
                    // Broken pieces no longer share one pinch event, even if their old label is the same. -xlinka
                    if (slackPiece)
                        continue;
                    // The link belongs to the strand at its strand end. A thread's thinnest link is mostly where it
                    // meets the bulk (the coat on a sole, the puddle), whose end is no strand: asking both ends to
                    // be strands, that link thinned to nothing and held the thread to the top of the lift.
                    bool meStrand = phase == FLUID_PHASE_STRAND;
                    bool heStrand = fluidPhaseOf(jPhase) == FLUID_PHASE_STRAND;
                    bool meCarrier = fluidCarrier(P.phase);
                    bool heCarrier = fluidCarrier(jPhase);
                    // a ligament straight from root to root (no carrier at either end) is a thread of its own and
                    // snaps on its own in D
                    if (!meCarrier && !heCarrier)
                        continue;
                    uint owner = meCarrier ? label : byId.v[j * FLUID_BY_ID_STRIDE + FLUID_ID_LABEL_NEXT];
                    bool aged = (!meStrand || myAge >= FLUID_STRAND_SNAP_AGE)
                        && (!heStrand || (acc.a[j].strandAge & FLUID_STRAND_AGE_MASK) >= FLUID_STRAND_SNAP_AGE);
                    if (owner != FLUID_NONE && aged && (acc.a[owner].strandAge & FLUID_STRAND_SNAPPED) == 0u)
                        atomicMax(byId.v[owner * FLUID_BY_ID_STRIDE + FLUID_ID_SNAP_BEST], fluidSnapKey(s, id, j));
                    continue;
                }
                float len = distance(P.position, pa.p[js].position);
                // Breakup history changes the failure test, never the resolution of a surviving link. -xlinka
                if (poolOpen && len > REFINE_LENGTH * d && len > longest)
                {
                    longest = len;
                    refineKey = id * 16u + k;
                    refinePartner = j;
                }
            }
            if (refineKey != FLUID_NONE && byId.v[b + FLUID_ID_REFINE_CLAIM] == refineKey
                && byId.v[refinePartner * FLUID_BY_ID_STRIDE + FLUID_ID_REFINE_CLAIM] == refineKey)
            {
                byId.v[b + FLUID_ID_REFINE_KEY] = refineKey;
                refine = true;
            }
        }
    }
    if (refine)
        atomicAdd(sRefines, 1u);
    barrier();
    if (lid == 0u && gl_WorkGroupID.x < FLUID_K_CHUNKS_MAX)
        candidates.v[FLUID_K_REFINE_COUNTS + gl_WorkGroupID.x] = sRefines;
}
