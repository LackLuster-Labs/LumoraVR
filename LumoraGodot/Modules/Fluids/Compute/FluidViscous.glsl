// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Extra passes for the thick kinds, in pairs: even passes read A and write B, odd passes read B and write A, so the
// result lands back in A. A particle whose substance wants fewer passes copies through.
//   Velocity passes, after Update (unbonded kinds past viscous saturation): one XSPH pass over the list.
//   Position passes, between Correct and Update (Pass carries FLUID_PASS_POSITIONS; bonded kinds): one more Jacobi
//   pass over the bonds, ping-ponging the position through aux's second word (free between Correct and Update; Update
//   writes it next), with the contact plane re-projected. One correction pass moves an elastic network's information
//   one bond, so its stiffness grows with the passes squared: at 2-4 a substep a 10 cm honey blob had an effective
//   modulus of a few kPa and slumped like water in 0.1 s whatever its bonds' own. They run on positions so Update
//   derives velocity from where the particles end up: run on velocity after Update, the next Correct took back what
//   they added and a resting puddle read 2-3 cm/s while it stood still. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_PB
FLUID_DECLARE_IDA
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_BONDS
FLUID_DECLARE_ANCHORS
FLUID_DECLARE_AUX
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_PUSH

shared vec4 sA[FLUID_GROUP_SIZE];

#define BOND_PASS_SOR 1.0

vec3 velocityOf(uint j, uint awake, bool fromB)
{
    if (j >= awake)
        return vec3(0.0);
    return fromB ? pb.p[j].velocity : pa.p[j].velocity;
}

void main()
{
    uint gid = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    uint i = gid / FLUID_LANES;
    uint lane = gid % FLUID_LANES;
    uint awake = counters.v[FLUID_C_AWAKE];
    bool inRange = i < awake;
    uint si = inRange ? i : 0u;
    bool fromB = (pc.c.pass & 1u) != 0u;

    FluidParticle P = pa.p[si];
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    bool positions = (pc.c.pass & FLUID_PASS_POSITIONS) != 0u;
    uint pass = pc.c.pass & 0xFFu;
    uint id = idA.v[si];
    bool held = (anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE;
    bool bondedKind = sub.bondCompliance > 0.0;
    if (positions)
    {
        if (!inRange || lane != 0u)
            return;
        bool even = (pass & 1u) == 0u;
        uint at = id * FLUID_AUX_STRIDE + 1u;
        vec3 xi = even ? P.position : aux.v[at].xyz;
        bool move = pass / 2u < sub.viscousPasses && bondedKind && !held && fluidPhaseOf(P.phase) != FLUID_PHASE_STRAND;
        vec3 xn = xi;
        if (move)
        {
            uint slotCount = hdr.h.bondSlots;
            float d = sub.spacing;
            float dtS = hdr.h.dtSubstep;
            float wI = 1.0 / max(P.mass, 1e-12);
            float cured = sub.cureTime > 0.0 ? clamp(P.age / sub.cureTime, 0.0, 1.0) : 0.0;
            float compliance = sub.bondCompliance * (1.0 - 0.9 * cured);
            vec3 sum = vec3(0.0);
            float bonded = 0.0;
            for (uint k = 0u; k < slotCount; k++)
            {
                FluidBond bnd = bonds.b[id * slotCount + k];
                if (bnd.partner == FLUID_NONE)
                    continue;
                uint js = idToSlot.v[bnd.partner];
                if (js == FLUID_NONE || fluidPhaseOf(pa.p[js].phase) == FLUID_PHASE_DEAD)
                    continue;
                bool partnerAwake = js < awake;
                bool kinematic = !partnerAwake || (anchors.a[bnd.partner].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE;
                vec3 xj = !even && partnerAwake ? aux.v[bnd.partner * FLUID_AUX_STRIDE + 1u].xyz : pa.p[js].position;
                float wJ = kinematic ? 0.0 : 1.0 / max(pa.p[js].mass, 1e-12);
                // Match the main solve: a drawn link gets thinner and cannot keep its bulk stiffness. -xlinka
                float stretched = max(bnd.restLength / d, 1.0);
                sum += fluidBondCorrection(xi - xj, bnd.restLength, 0.0, compliance * stretched * stretched, dtS, wI, wJ).xyz;
                bonded += 1.0;
            }
            if (bonded > 0.0)
                xn = xi + fluidAverage(sum * (BOND_PASS_SOR / FLUID_SOR), bonded, d);
            vec4 plane = slots.v[i * FLUID_SLOT_STRIDE + 1u];
            if (dot(plane.xyz, plane.xyz) > 0.5)
                xn += plane.xyz * max(0.5 * d - (dot(xn, plane.xyz) - plane.w), 0.0);
        }
        if (even)
            aux.v[at].xyz = xn;
        else if (move)
            pa.p[i].position = xn;
        return;
    }
    bool wanted = inRange && pass / 2u < sub.viscousPasses && !held && !bondedKind;
    float h = sub.smoothingRadius;
    float h2 = h * h;
    float poly6 = fluidPoly6Coefficient(h);
    vec3 x = P.position;
    vec3 vi = velocityOf(si, awake, fromB);
    uint count = wanted ? floatBitsToUint(slots.v[si * FLUID_SLOT_STRIDE].z) : 0u;

    vec3 dv = vec3(0.0);
    for (uint e = lane; e < count; e += FLUID_LANES)
    {
        uint j = lists.v[si * FLUID_MAX_NEIGHBOURS + e];
        vec3 r = x - pa.p[j].position;
        float r2 = dot(r, r);
        if (r2 >= h2)
            continue;
        dv += pa.p[j].volume * fluidPoly6(r2, h2, poly6) * (velocityOf(j, awake, fromB) - vi);
    }
    sA[lid] = vec4(dv, 0.0);
    barrier();
    for (uint off = FLUID_LANES / 2u; off > 0u; off >>= 1)
    {
        if (lane < off)
            sA[lid] += sA[lid + off];
        barrier();
    }
    if (!inRange || lane != 0u)
        return;

    vec3 vn = vi;
    if (wanted)
    {
        float c = fluidXsphStrength(sub.xsphFactor, hdr.h.dtSubstep);
        vn += c * sA[lid].xyz;
        uint slotCount = hdr.h.bondSlots;
        vec3 along = vec3(0.0);
        float bonded = 0.0;
        for (uint k = 0u; k < slotCount; k++)
        {
            uint partner = bonds.b[id * slotCount + k].partner;
            if (partner == FLUID_NONE)
                continue;
            uint js = idToSlot.v[partner];
            if (js == FLUID_NONE)
                continue;
            vec3 d = pa.p[js].position - x;
            float len = length(d);
            if (len < 1e-9)
                continue;
            d /= len;
            along += d * dot(velocityOf(js, awake, fromB) - vi, d);
            bonded += 1.0;
        }
        if (bonded > 0.0)
            vn += c * along / bonded;
    }
    if (fromB)
        pa.p[i].velocity = vn;
    else
        pb.p[i].velocity = vn;
}
