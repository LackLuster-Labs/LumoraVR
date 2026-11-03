// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Frame end A, one thread per awake slot: the NaN guard, ageing (fade, drying of stray drops, drains, the kill
// plane), strand classification, per-link strain and thinning with its volume moves into the nL accumulators, one
// label round, slack pieces, the frame's ligament list for B, refine claims, bond proposals, anchor candidates,
// sleep marks, and the frame's stats and bakes.
//
// Each link is handled once, by its lower id end, which writes both ends' copies of the record. Volume moves go
// through integer atomics and are applied by B, so their order never matters. Claims and labels are minima and the
// snap pick a maximum: every atomic here is order-free, which is what keeps a replay bit-identical. So is the new
// phase: neighbours read each other's phase here, so A leaves the old one in place and B commits the new one (it
// waits in aux's neighbour word, which nothing reads between them). A particle that
// dies here is only marked (dead and dying) and keeps its bonds until the next commands pass. -xlinka

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
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_AUX
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_SYSTEMS
FLUID_DECLARE_FRAMES
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_PROXIES
FLUID_DECLARE_BONES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_BY_ID
FLUID_DECLARE_CANDIDATES
FLUID_DECLARE_SUMMARY
FLUID_DECLARE_REGIONS
FLUID_DECLARE_HIDDEN
FLUID_DECLARE_PUSH

shared uint sCandidates;

// bonds within 35 degrees of opposite each other make a chain
#define CHAIN_COS 0.819
#define STRAND_ENTER_NEIGHBOURS 8.0
#define STRAND_LEAVE_NEIGHBOURS 12.0
#define DROP_NEIGHBOURS 4.0
#define BOND_REACH 1.2
#define REFINE_LENGTH 1.5
#define MAX_ANCHOR_NORMAL_SPEED 3.0
#define BAKE_SECONDS 1.5
#define BAKE_SPEED 0.02
// spacings: a link this long with an end in the air is a ligament however slowly it was drawn
#define LIGAMENT_LENGTH 4.0
// bulk neighbours from which a strand rests in goo
#define RESTING_BULK 4u

void framePose(uint f, float t, out vec4 q, out vec3 position, out float scale)
{
    if (f == 0u || f >= hdr.h.frames)
    {
        q = vec4(0.0, 0.0, 0.0, 1.0);
        position = vec3(0.0);
        scale = 1.0;
        return;
    }
    FluidFrameRecord F = frames.f[f];
    q = fluidQuatNlerp(F.lastRotation, F.rotation, t);
    position = mix(F.lastPosition, F.position, t);
    scale = mix(F.lastScale, F.scale, t);
}

uint limbOf(uint part)
{
    if (part == 0u || part == 2u || part == 4u)
        return 0u;
    if (part == 1u || part == 3u || part == 5u)
        return 1u;
    if (part == 8u || part == 10u)
        return 2u;
    if (part == 9u || part == 11u)
        return 3u;
    return FLUID_NONE;
}

// Half the summed lengths of a particle's live links: the length its volume is spread along as a strand.
float halfLength(uint id, vec3 x, uint slotCount)
{
    float sum = 0.0;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint partner = bonds.b[id * slotCount + k].partner;
        if (partner == FLUID_NONE)
            continue;
        uint js = idToSlot.v[partner];
        if (js == FLUID_NONE)
            continue;
        sum += distance(x, pa.p[js].position);
    }
    return 0.5 * sum;
}

float linkBadness(float radius, uint strainFlags, FluidSubstanceRecord sub)
{
    float s = sub.minThread / max(radius, 1e-9);
    if (sub.stretchLimit > 0.0)
        s = max(s, fluidStrainOf(strainFlags) / sub.stretchLimit);
    return s;
}

uint backSlot(uint partner, uint id, uint slotCount)
{
    for (uint k = 0u; k < slotCount; k++)
    {
        if (bonds.b[partner * slotCount + k].partner == id)
            return partner * slotCount + k;
    }
    return FLUID_NONE;
}

float neighboursOf(FluidParticle q)
{
    return float((q.phase >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u);
}

// Held by something that moves (a prop, a body part): a long link from it is a ligament drawn off that surface, however
// many neighbours its end still has. Goo held by the floor is not, or every stretched link in a puddle would be one.
bool heldByMover(uint pid)
{
    uint kind = anchors.a[pid].kindAge & FLUID_ANCHOR_KIND_MASK;
    return kind == FLUID_ANCHOR_RIGID || kind == FLUID_ANCHOR_BONE || kind == FLUID_ANCHOR_SKIN;
}

// Two moving supports can draw a bridge even while both roots retain dense coats. -xlinka
bool separateMovingSupports(uint a, uint b)
{
    bool movingA = heldByMover(a), movingB = heldByMover(b);
    if (movingA != movingB)
        return true;
    if (!movingA)
        return false;
    FluidAnchor first = anchors.a[a], second = anchors.a[b];
    uint kind = first.kindAge & FLUID_ANCHOR_KIND_MASK;
    return kind != (second.kindAge & FLUID_ANCHOR_KIND_MASK) || first.target != second.target
        || (kind == FLUID_ANCHOR_RIGID && first.triangle != second.triangle);
}

// Held by a static surface: the floor under a puddle is its boundary, so a particle on it counts as in the bulk however
// few neighbours it has.
bool heldByFloor(uint pid)
{
    return (anchors.a[pid].kindAge & FLUID_ANCHOR_KIND_MASK) == FLUID_ANCHOR_STATIC;
}

// A supported sheet can have few neighbours without being a free thread. Use the current contact plane. -xlinka
bool sameStaticPlane(uint i, vec3 x, uint j, vec3 y, uint awake, float d)
{
    if (i >= awake || j >= awake || fluidFrameOf(pa.p[i].material) != fluidFrameOf(pa.p[j].material))
        return false;
    uint ib = i * FLUID_SLOT_STRIDE, jb = j * FLUID_SLOT_STRIDE;
    uint key = floatBitsToUint(slots.v[ib + 2u].w);
    vec4 a = slots.v[ib + 1u], b = slots.v[jb + 1u];
    float reach = (0.5 + FLUID_CONTACT_SLOP) * d;
    return (key >> FLUID_TARGET_KIND_SHIFT) == FLUID_ANCHOR_STATIC
        && key == floatBitsToUint(slots.v[jb + 2u].w)
        && dot(a.xyz, b.xyz) > 0.9
        && abs(dot(x, a.xyz) - a.w) <= reach && abs(dot(y, b.xyz) - b.w) <= reach
        && abs(dot(x - y, a.xyz)) < d;
}

void markDead(inout FluidParticle q)
{
    if ((q.phase & FLUID_PARTICLE_REFINED) != 0u)
        atomicAdd(counters.v[FLUID_C_POOL], 0xFFFFFFFFu);
    q.phase = FLUID_PHASE_DEAD | FLUID_PARTICLE_DYING;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    if (lid == 0u)
        sCandidates = 0u;
    barrier();

    uint awake = counters.v[FLUID_C_AWAKE];
    bool inRange = i < awake;
    bool candidate = false;
    if (inRange)
    {
        FluidParticle P = pa.p[i];
        uint id = idA.v[i];
        uint phaseWord = P.phase;
        uint phase = fluidPhaseOf(P.phase);
        FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
        float d = sub.spacing;
        float dtF = hdr.h.dtFrame;
        uint slotCount = hdr.h.bondSlots;
        uint sys = min(fluidSystemOf(P.material), FLUID_MAX_SYSTEMS - 1u);
        uint f = fluidFrameOf(P.material);
        bool inFrame = f != 0u && f < hdr.h.frames;
        vec4 fq;
        vec3 fpos;
        float fscale;
        framePose(f, 1.0, fq, fpos, fscale);
        uint b = id * FLUID_BY_ID_STRIDE;
        byId.v[b + FLUID_ID_SNAP_BEST] = 0u;
        for (uint k = 0u; k < FLUID_ID_PROPOSAL_COUNT; k++)
            byId.v[b + FLUID_ID_PROPOSALS + k] = FLUID_NONE;
        acc.a[id].claim = 0u;

        if (phase != FLUID_PHASE_DEAD)
        {
            // NaN guard: back to where the substep started, standing still.
            bvec3 badX = bvec3(isnan(P.position.x) || isinf(P.position.x), isnan(P.position.y) || isinf(P.position.y), isnan(P.position.z) || isinf(P.position.z));
            bvec3 badV = bvec3(isnan(P.velocity.x) || isinf(P.velocity.x), isnan(P.velocity.y) || isinf(P.velocity.y), isnan(P.velocity.z) || isinf(P.velocity.z));
            if (any(badX) || any(badV) || isnan(P.volume))
            {
                bool previousGood = !(isnan(P.previous.x) || isnan(P.previous.y) || isnan(P.previous.z) || isinf(P.previous.x) || isinf(P.previous.y) || isinf(P.previous.z));
                P.position = previousGood ? P.previous : systems.s[sys].boundsCenter;
                P.previous = P.position;
                P.velocity = vec3(0.0);
                if (isnan(P.volume) || P.volume <= 0.0)
                    P.volume = d * d * d;
                atomicAdd(stats.v[FLUID_S_SYSTEM + sys * FLUID_S_SYSTEM_STRIDE + FLUID_S_SYS_NAN], 1u);
            }

            P.age += dtF;
            float v0 = d * d * d;
            vec3 xw = inFrame ? fluidToWorld(P.position, fq, fpos, fscale) : P.position;
            if ((P.phase & FLUID_PARTICLE_FADING) != 0u)
            {
                P.volume *= exp(-dtF / max(P.fadeTime, 0.01));
                if (P.volume < 0.05 * v0)
                    markDead(P);
            }
            if (fluidPhaseOf(P.phase) == FLUID_PHASE_DROP && sub.dryTime > 0.0)
            {
                P.volume *= exp(-dtF / sub.dryTime);
                if (P.volume < 0.1 * v0)
                    markDead(P);
            }
            float killBelow = systems.s[sys].killBelow;
            if (killBelow != 0.0 && xw.y < killBelow)
                markDead(P);
            phase = fluidPhaseOf(P.phase);
        }

        if (phase != FLUID_PHASE_DEAD)
        {
            // Classification. Chain bonds: one bond, or two within 35 degrees of opposite.
            float neighbours = aux.v[id * FLUID_AUX_STRIDE].w;
            uint bondCount = 0u;
            bool supportedSheet = sameStaticPlane(i, P.position, i, P.position, awake, d);
            vec3 dirs[2];
            for (uint k = 0u; k < slotCount; k++)
            {
                uint partner = bonds.b[id * slotCount + k].partner;
                if (partner == FLUID_NONE)
                    continue;
                uint js = idToSlot.v[partner];
                if (js == FLUID_NONE)
                    continue;
                supportedSheet = supportedSheet && sameStaticPlane(i, P.position, js, pa.p[js].position, awake, d);
                if (bondCount < 2u)
                    dirs[bondCount] = normalize(pa.p[js].position - P.position + vec3(1e-12));
                bondCount++;
            }
            supportedSheet = supportedSheet && bondCount > 0u;
            bool chain = bondCount == 1u || (bondCount == 2u && dot(dirs[0], dirs[1]) < -CHAIN_COS);
            uint newPhase = phase;
            if (supportedSheet)
                newPhase = FLUID_PHASE_BULK;
            else if (phase == FLUID_PHASE_BULK || phase == FLUID_PHASE_DROP)
            {
                if (neighbours < STRAND_ENTER_NEIGHBOURS && chain)
                    newPhase = FLUID_PHASE_STRAND;
                else if (bondCount == 0u && neighbours < DROP_NEIGHBOURS)
                    newPhase = FLUID_PHASE_DROP;
                else if (phase == FLUID_PHASE_DROP && neighbours >= DROP_NEIGHBOURS + 2.0)
                    newPhase = FLUID_PHASE_BULK;
            }
            else if (phase == FLUID_PHASE_STRAND)
            {
                if (neighbours >= STRAND_LEAVE_NEIGHBOURS || bondCount == 0u)
                    newPhase = bondCount == 0u && neighbours < DROP_NEIGHBOURS ? FLUID_PHASE_DROP : FLUID_PHASE_BULK;
            }
            phase = newPhase;
            uint chainBits = chain ? min(bondCount, 2u) : 0u;
            // a strand with bulk goo around it rests in it (FLUID_PARTICLE_RESTING): its neighbours from the list,
            // their phases as frame end A found them (stable in this pass)
            bool resting = supportedSheet;
            if (phase == FLUID_PHASE_STRAND)
            {
                uint listed = floatBitsToUint(slots.v[i * FLUID_SLOT_STRIDE].z);
                uint bulkAround = 0u;
                for (uint e = 0u; e < listed; e++)
                {
                    uint ks = lists.v[i * FLUID_MAX_NEIGHBOURS + e];
                    if (ks < awake && fluidPhaseOf(pa.p[ks].phase) == FLUID_PHASE_BULK)
                        bulkAround++;
                }
                resting = bulkAround >= RESTING_BULK;
            }
            P.phase = (P.phase & ~(FLUID_PHASE_MASK | (255u << FLUID_NEIGHBOUR_COUNT_SHIFT) | (3u << FLUID_CHAIN_BOND_SHIFT) | FLUID_PARTICLE_NEW | FLUID_PARTICLE_SLACK | FLUID_PARTICLE_RESTING))
                    | phase | (uint(min(neighbours, 255.0)) << FLUID_NEIGHBOUR_COUNT_SHIFT) | (chainBits << FLUID_CHAIN_BOND_SHIFT)
                    | (resting ? FLUID_PARTICLE_RESTING : 0u);
            uint ageWord = acc.a[id].strandAge;
            // The snapped bit is only ever cleared by the next commands pass: neighbours read it here, so clearing
            // it in this pass would race them.
            acc.a[id].strandAge = (ageWord & ~FLUID_STRAND_AGE_MASK)
                | (phase == FLUID_PHASE_STRAND ? min((ageWord & FLUID_STRAND_AGE_MASK) + 1u, FLUID_STRAND_AGE_MASK) : 0u);
            // A thread's label runs between its carriers (fluidCarrier: strands and free bulk nodes in the air), never
            // through the coat on a sole or the puddle where every thread ends; anything else labels itself.
            uint myLabel = acc.a[id].label;
            bool meCarrier = !supportedSheet && fluidCarrier((P.phase & ~FLUID_PARTICLE_ANCHORED)
                | ((anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE ? FLUID_PARTICLE_ANCHORED : 0u));
            // A snapped thread's pieces stay slack after they bunch up and stop being carriers, until they are back
            // deep in the bulk or on a supported sheet: by the label's mark, or their own slack bit. -xlinka
            bool snappedPiece = (acc.a[myLabel].strandAge & FLUID_STRAND_SNAPPED) != 0u || (phaseWord & FLUID_PARTICLE_SLACK) != 0u;
            bool slack = !supportedSheet && neighbours < STRAND_LEAVE_NEIGHBOURS && snappedPiece;
            if (slack)
                P.phase |= FLUID_PARTICLE_SLACK;
            bool inThread = meCarrier;

            // The owning stored end updates strain, the strand flag, thinning and volume moves. -xlinka
            float myHalf = halfLength(id, P.position, slotCount);
            float myRadius = fluidStrandRadius(P.volume, myHalf, d);
            uint labelNext = myLabel;
            float longest = 0.0;
            uint refineKey = FLUID_NONE;
            uint refinePartner = FLUID_NONE;
            bool poolOpen = counters.v[FLUID_C_POOL] < pc.c.arg;
            for (uint k = 0u; k < slotCount; k++)
            {
                uint at = id * slotCount + k;
                FluidBond bnd = bonds.b[at];
                if (bnd.partner == FLUID_NONE)
                    continue;
                uint j = bnd.partner;
                uint js = idToSlot.v[j];
                if (js == FLUID_NONE)
                    continue;
                FluidParticle Q = pa.p[js];
                uint qPhase = fluidPhaseOf(Q.phase);
                if (qPhase == FLUID_PHASE_DEAD)
                    continue;
                vec3 dv = P.position - Q.position;
                float len = length(dv);
                // A thread link has a strand at either end, or is a ligament: a bonded kind's link between two bulk
                // ends drawn out past 1.5 d, into the air (an end with few neighbours) or off a surface that moves
                // (one end held by a prop or a body part, the other free). A ligament thins and snaps as a thread but
                // gains free interior particles while it survives. B merges overlapping free ligaments. The owner
                // keeps its own flag: a link becomes a ligament only while it is drawn, its ends parting faster
                // than it could thin by itself (rate lambda over 2/3), and stays one. A link a puddle's rim stretches
                // slowly past 1.5 d is creep, not a thread. -xlinka
                float qNeighbours = neighboursOf(Q);
                bool meStrand = phase == FLUID_PHASE_STRAND;
                bool qStrand = qPhase == FLUID_PHASE_STRAND;
                // A label crosses a link between two carriers that is a strand's or drawn past 1.5 d: both ends
                // decide it from positions and phases alone, never from the owner's flag, which it rewrites here.
                bool samePlane = sameStaticPlane(i, P.position, js, Q.position, awake, d);
                bool qCarrier = !samePlane && fluidCarrier(Q.phase);
                uint qLab = acc.a[j].label;
                bool qSlack = !samePlane && qNeighbours < STRAND_LEAVE_NEIGHBOURS
                    && ((acc.a[qLab].strandAge & FLUID_STRAND_SNAPPED) != 0u || (Q.phase & FLUID_PARTICLE_SLACK) != 0u);
                // and never between a snapped piece and live goo: a live thread that touched a slack piece took its
                // label, lost it with the mark, and the same label snapped twice
                if (meCarrier && qCarrier && slack == qSlack && (meStrand || qStrand || len > REFINE_LENGTH * d))
                    labelNext = min(labelNext, qLab);
                // The awake end owns a sleeping partner; a one-sided link belongs to its stored end. -xlinka
                if (id > j && js < awake && backSlot(j, id, slotCount) != FLUID_NONE)
                    continue;
                vec3 dir = len > 1e-12 ? dv / len : vec3(0.0, 1.0, 0.0);
                float rate = dot(P.velocity - Q.velocity, dir) / max(len, 1e-6);
                // Out of the bulk: not both ends deep in it (a long link between two deep particles is the bulk
                // flowing, and Update's re-forming lets it go).
                bool inAir = !samePlane && (separateMovingSupports(id, j)
                    || min(heldByFloor(id) ? STRAND_LEAVE_NEIGHBOURS : neighbours, heldByFloor(j) ? STRAND_LEAVE_NEIGHBOURS : qNeighbours) < STRAND_LEAVE_NEIGHBOURS);
                bool wasLigament = (bnd.strainFlags & FLUID_BOND_STRAND) != 0u;
                bool ligament = !meStrand && !qStrand && sub.bondCompliance > 0.0 && len > REFINE_LENGTH * d && inAir
                    && (wasLigament || rate * sub.relaxation > 2.0 / 3.0 || len > LIGAMENT_LENGTH * d);
                bool strandLink = !samePlane && (meStrand || qStrand || ligament);
                float strain;
                if (sub.relaxation > 0.0)
                    strain = fluidStrainOf(bnd.strainFlags) * exp(-dtF / sub.relaxation) + rate * dtF;
                else
                    strain = len / max(bnd.restLength, 1e-9) - 1.0;
                uint flags = bnd.strainFlags;
                float radius = bnd.radius;
                bool slackHere = slack || qSlack;
                if (strandLink)
                {
                    float qRadius = fluidStrandRadius(Q.volume, halfLength(j, Q.position, slotCount), d);
                    float cap = min(myRadius, qRadius);
                    float noise = 0.95 + 0.1 * fluidRandom(min(id, j), max(id, j));
                    if ((flags & FLUID_BOND_STRAND) == 0u)
                    {
                        // a link turning into a strand starts at its ends' radius, with seeded noise so a thread
                        // does not pinch everywhere at once
                        radius = min(cap, radius > 0.0 ? radius : 0.5 * d) * noise;
                        flags |= FLUID_BOND_STRAND;
                    }
                    float modulus = sub.bondCompliance > 0.0 ? 1.0 / (sub.bondCompliance * d) : 0.0;
                    bool elasto = (flags & FLUID_BOND_ELASTO) != 0u || fluidElastoCrossed(sub.relaxation, modulus, strain, sub.surfaceTension, radius);
                    if (elasto)
                        flags |= FLUID_BOND_ELASTO;
                    uint regime = fluidThinningRegime(elasto, sub.viscosity, sub.density, sub.surfaceTension, radius);
                    // Recoiling elastic pieces recover before necking. Once recoil stops, capillarity still
                    // acts on their tails; a previous pinch must not make the remaining liquid immortal. -xlinka
                    bool slackLink = slackHere;
                    // A strand link between two particles at rest in goo (neither a carrier) is a piece of thread that
                    // came to rest: it has goo all round it and does not neck.
                    bool resting = (meStrand || qStrand) && !meCarrier && !qCarrier;
                    bool held = !resting && (sub.relaxation <= 0.0 || (slackLink ? rate >= 0.0 : strain > 0.0));
                    // A ligament whose ends part faster than it could thin by itself (Wi = rate lambda over 2/3) is
                    // drawn: as thick as its goo over its length. Once the pull slows below that, elastocapillary
                    // thinning takes over from there (Entov and Hinch 1997), so a ligament starts to neck a few
                    // centimetres into a lift, not the moment it forms.
                    bool drawn = ligament && sub.relaxation > 0.0 && rate * sub.relaxation > 2.0 / 3.0;
                    float thinned = held && !drawn ? fluidThinnedRadius(regime, radius, dtF, sub.viscosity, sub.density, sub.surfaceTension, sub.relaxation) : radius;
                    if (sub.relaxation > 0.0 && thinned < radius)
                        strain += 2.0 * (radius - thinned) / max(radius, 1e-9);
                    // The neck's lost volume goes to the thicker end: Laplace pressure is higher in the thin end,
                    // so beads grow where the thread is thick and at its roots. Only along strands: a ligament's
                    // goo is its ends', and a stuck end is the film a surface keeps, never drained into a thread.
                    float moved = 0.5 * fluidLinkVolumeLoss(radius, thinned, len);
                    if (moved > 0.0 && abs(myRadius - qRadius) > 1e-9 && (meStrand || qStrand))
                    {
                        bool iThinner = myRadius < qRadius;
                        uint donor = iThinner ? id : j;
                        float donorVolume = iThinner ? P.volume : Q.volume;
                        int nl = fluidToNanolitres(min(moved, FLUID_MAX_VOLUME_MOVE * donorVolume));
                        if (nl > 0 && (anchors.a[donor].kindAge & FLUID_ANCHOR_KIND_MASK) == FLUID_ANCHOR_NONE)
                        {
                            atomicAdd(acc.a[donor].volumeNl, -nl);
                            atomicAdd(acc.a[iThinner ? j : id].volumeNl, nl);
                        }
                    }
                    // A thread is never thicker than its volume over its length (r = sqrt(V / (pi L)) at its thinner
                    // end), so a fibre drawn too long for what it carries reaches MinThread and snaps on that alone.
                    radius = drawn ? cap * noise : min(thinned, cap);
                }
                else
                    flags &= ~FLUID_BOND_STRAND;
                flags &= ~FLUID_BOND_MERGED;
                if (ligament)
                {
                    // listed for B, which merges parallel ones
                    uint row = atomicAdd(candidates.v[FLUID_K_LIGAMENT_COUNT], 1u);
                    if (row < FLUID_K_LIGAMENT_CAP)
                    {
                        uint e = FLUID_K_LIGAMENTS + row * FLUID_K_LIGAMENT_STRIDE;
                        candidates.v[e] = id;
                        candidates.v[e + 1u] = j;
                        candidates.v[e + 2u] = floatBitsToUint(P.position.x);
                        candidates.v[e + 3u] = floatBitsToUint(P.position.y);
                        candidates.v[e + 4u] = floatBitsToUint(P.position.z);
                        candidates.v[e + 5u] = floatBitsToUint(Q.position.x);
                        candidates.v[e + 6u] = floatBitsToUint(Q.position.y);
                        candidates.v[e + 7u] = floatBitsToUint(Q.position.z);
                    }
                }
                flags = fluidWithStrain(flags, strain);
                bonds.b[at].radius = radius;
                bonds.b[at].strainFlags = flags | FLUID_BOND_OWNER;
                uint back = backSlot(j, id, slotCount);
                if (back != FLUID_NONE)
                {
                    bonds.b[back].radius = radius;
                    bonds.b[back].strainFlags = flags & ~FLUID_BOND_OWNER;
                }
                // Healthy long links need free interior motion during recoil and rest as well as extension. -xlinka
                float refineFailure = slackHere ? sub.minThread / max(radius, 1e-9) : linkBadness(radius, flags, sub);
                if (strandLink && poolOpen && len > REFINE_LENGTH * d && len > longest && refineFailure < 1.0)
                {
                    longest = len;
                    refineKey = id * 16u + k;
                    refinePartner = j;
                }
            }
            if (refineKey != FLUID_NONE)
            {
                atomicMin(byId.v[b + FLUID_ID_REFINE_CLAIM], refineKey);
                atomicMin(byId.v[refinePartner * FLUID_BY_ID_STRIDE + FLUID_ID_REFINE_CLAIM], refineKey);
            }
            if (inThread)
                labelNext = min(labelNext, acc.a[labelNext].label);
            else
                labelNext = slack ? myLabel : id;
            byId.v[b + FLUID_ID_LABEL_NEXT] = labelNext;

            // Bond proposals: the nearest unbonded neighbours of the same substance inside 1.2 d. B makes a bond
            // only where two particles proposed each other.
            if (sub.bondCompliance > 0.0 && phase != FLUID_PHASE_STRAND && bondCount < slotCount)
            {
                uint want = min(slotCount - bondCount, FLUID_ID_PROPOSAL_COUNT);
                float bestD[4] = float[4](1e30, 1e30, 1e30, 1e30);
                uint bestId[4] = uint[4](FLUID_NONE, FLUID_NONE, FLUID_NONE, FLUID_NONE);
                uint count = floatBitsToUint(slots.v[i * FLUID_SLOT_STRIDE].z);
                float reach2 = BOND_REACH * BOND_REACH * d * d;
                for (uint e = 0u; e < count; e++)
                {
                    uint js = lists.v[i * FLUID_MAX_NEIGHBOURS + e];
                    if (js >= awake)
                        continue;
                    FluidParticle Q = pa.p[js];
                    if (fluidSubstanceOf(Q.material) != fluidSubstanceOf(P.material) || fluidPhaseOf(Q.phase) == FLUID_PHASE_STRAND || fluidPhaseOf(Q.phase) == FLUID_PHASE_DEAD)
                        continue;
                    vec3 dq = Q.position - P.position;
                    float dd = dot(dq, dq);
                    if (dd >= reach2)
                        continue;
                    uint jid = idA.v[js];
                    bool already = false;
                    for (uint k = 0u; k < slotCount; k++)
                        already = already || bonds.b[id * slotCount + k].partner == jid;
                    if (already)
                        continue;
                    for (uint q = 0u; q < want; q++)
                    {
                        if (dd < bestD[q])
                        {
                            for (uint m = want - 1u; m > q; m--)
                            {
                                bestD[m] = bestD[m - 1u];
                                bestId[m] = bestId[m - 1u];
                            }
                            bestD[q] = dd;
                            bestId[q] = jid;
                            break;
                        }
                    }
                }
                for (uint q = 0u; q < FLUID_ID_PROPOSAL_COUNT; q++)
                    byId.v[b + FLUID_ID_PROPOSALS + q] = bestId[q];
            }

            // Anchor candidates: in the band of a surface that will hold it, for StickTime, not slamming into it.
            FluidAnchor A = anchors.a[id];
            uint akind = A.kindAge & FLUID_ANCHOR_KIND_MASK;
            uint sb = i * FLUID_SLOT_STRIDE;
            uint key = floatBitsToUint(slots.v[sb + 2u].w);
            if (akind == FLUID_ANCHOR_NONE && sub.anchorStrength > 0.0 && key != 0u && phase != FLUID_PHASE_STRAND)
            {
                float edge = slots.v[sb + 3u].z - 0.5 * d;
                float stick = slots.v[sb + 3u].y;
                uint kind = key >> FLUID_TARGET_KIND_SHIFT;
                if (kind == FLUID_ANCHOR_BONE)
                {
                    uint proxy = key & FLUID_TARGET_INDEX_MASK;
                    uint person = proxies.p[proxy].person;
                    uint pflags = person < hdr.h.people ? people.p[person].flags : FLUID_PERSON_FROZEN;
                    if ((pflags & FLUID_PERSON_STICK_MASK) != 0u || (pflags & FLUID_PERSON_FROZEN) != 0u)
                        stick = 0.0;
                    if ((pflags & FLUID_PERSON_LOCAL) != 0u)
                    {
                        uint origin = fluidOriginOf(P.material);
                        for (uint k = 0u; k < hdr.h.hiddenOrigins; k++)
                            stick = hidden.v[k] == origin ? 0.0 : stick;
                    }
                }
                if (edge <= sub.stickDistance + FLUID_CONTACT_SLOP * d && stick > 0.0)
                {
                    P.bandTime += dtF;
                    vec3 nrm = slots.v[sb + 1u].xyz;
                    float approach = abs(dot(P.velocity - slots.v[sb + 2u].xyz, nrm));
                    if (P.bandTime >= sub.stickTime && approach < MAX_ANCHOR_NORMAL_SPEED)
                    {
                        candidate = true;
                        acc.a[id].claim = 1u;
                    }
                }
                else
                    P.bandTime = 0.0;
            }
            else if (akind == FLUID_ANCHOR_NONE)
                P.bandTime = 0.0;

            // Stats, anchors per target, stuck strength per limb, bakes.
            uint sbase = FLUID_S_SYSTEM + sys * FLUID_S_SYSTEM_STRIDE;
            float speed = length(P.velocity);
            atomicMax(stats.v[sbase + FLUID_S_SYS_VMAX], floatBitsToUint(speed));
            atomicAdd(stats.v[sbase + FLUID_S_SYS_ENERGY], fluidFixed(0.5 * P.mass * speed * speed, FLUID_ENERGY_UNIT));
            atomicAdd(stats.v[sbase + FLUID_S_SYS_AWAKE], 1u);
            if (phase == FLUID_PHASE_STRAND && labelNext == id)
                atomicAdd(stats.v[sbase + FLUID_S_SYS_STRANDS], 1u);
            bool movingTarget = false;
            if (akind != FLUID_ANCHOR_NONE)
            {
                atomicAdd(stats.v[sbase + FLUID_S_SYS_ANCHORS], 1u);
                uint targetSlot = (akind == FLUID_ANCHOR_BONE || akind == FLUID_ANCHOR_SKIN)
                    ? FLUID_TARGET_PEOPLE + min(A.target >> 8, FLUID_S_PEOPLE_MAX - 1u) * uint(FLUID_BODY_PARTS) + min(A.target & 255u, uint(FLUID_BODY_PARTS) - 1u)
                    : min(A.target, FLUID_TARGET_PEOPLE - 1u);
                atomicAdd(stats.v[FLUID_S_TARGETS + targetSlot], 1u);
                movingTarget = akind != FLUID_ANCHOR_STATIC;
                if (akind == FLUID_ANCHOR_BONE || akind == FLUID_ANCHOR_SKIN)
                {
                    uint person = A.target >> 8;
                    uint part = A.target & 255u;
                    uint limb = limbOf(part);
                    if (person < FLUID_S_PEOPLE_MAX && limb != FLUID_NONE)
                        atomicAdd(stats.v[FLUID_S_LIMBS + (person * uint(FLUID_LIMBS) + limb) * 4u], fluidFixed(A.strength, FLUID_STUCK_UNIT));
                    uint pflags = person < hdr.h.people ? people.p[person].flags : 0u;
                    // Bakes are the wearer's (their own body), or the keeper's for a view-only person in a system
                    // it keeps (coat proposals), on the frames the packer asks for them (4 Hz), first sub-frame only.
                    bool kept = (systems.s[sys].flags & FLUID_SYSTEM_KEPT) != 0u;
                    bool ours = (pflags & FLUID_PERSON_LOCAL) != 0u || ((pflags & FLUID_PERSON_VIEW_ONLY) != 0u && kept);
                    float ageSeconds = float(A.kindAge >> FLUID_ANCHOR_AGE_SHIFT) * dtF;
                    bool bakeFrame = (hdr.h.flags & FLUID_FRAME_BAKES) != 0u && (hdr.h.flags & FLUID_FRAME_FIRST_OF_BLOCK) != 0u;
                    if (bakeFrame && ours && ageSeconds >= BAKE_SECONDS && length(P.velocity - slots.v[sb + 2u].xyz) < BAKE_SPEED
                        && person < hdr.h.people && part < people.p[person].boneCount)
                    {
                        uint slot = atomicAdd(stats.v[FLUID_S_BAKES], 1u);
                        if (slot < uint(FLUID_SUMMARY_BAKES_CAPACITY))
                        {
                            // in the part bone's space, the space spots and the baker use, whatever the anchor held
                            uint pb = people.p[person].boneStart + part;
                            FluidBoneRecord PB = bones.b[min(pb, max(hdr.h.bones, 1u) - 1u)];
                            vec3 xb = inFrame ? fluidToWorld(P.position, fq, fpos, fscale) : P.position;
                            summary.bakes[slot].local = fluidAffineInverse(PB.row0, PB.row1, PB.row2, xb);
                            summary.bakes[slot].volume = P.volume;
                            summary.bakes[slot].id = id;
                            summary.bakes[slot].owner = (person << 16) | (part << 8) | fluidSubstanceOf(P.material);
                            summary.bakes[slot].tint = P.tint;
                            summary.bakes[slot].cell = fluidPackLedgerCell(P.position, systems.s[sys].ledgerCell);
                        }
                    }
                }
                uint age = min((A.kindAge >> FLUID_ANCHOR_AGE_SHIFT) + 1u, 0xFFFFFFu);
                anchors.a[id].kindAge = (A.kindAge & 255u) | (age << FLUID_ANCHOR_AGE_SHIFT);
            }

            // Sleep: any fast particle keeps its 4h region awake, and wakes the asleep regions it touches.
            float regionSize = 4.0 * sub.smoothingRadius;
            bool fast = speed > sub.sleepSpeed || movingTarget;
            uint region = fluidHashCell(fluidCellOf(P.position, 1.0 / regionSize), f, FLUID_REGION_TABLE - 1u);
            if (fast)
            {
                atomicOr(regions.r[region].awake, 1u);
                atomicMax(regions.r[region].maxSpeedBits, floatBitsToUint(speed));
                uint count = floatBitsToUint(slots.v[i * FLUID_SLOT_STRIDE].z);
                for (uint e = 0u; e < count; e++)
                {
                    uint js = lists.v[i * FLUID_MAX_NEIGHBOURS + e];
                    if (js < awake)
                        continue;
                    uint other = fluidHashCell(fluidCellOf(pa.p[js].position, 1.0 / regionSize), f, FLUID_REGION_TABLE - 1u);
                    if (other != region)
                        atomicOr(regions.r[other].awake, 2u);
                }
            }
        }
        if ((anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE && fluidPhaseOf(P.phase) != FLUID_PHASE_DEAD)
            P.phase |= FLUID_PARTICLE_ANCHORED;
        else
            P.phase &= ~FLUID_PARTICLE_ANCHORED;
        aux.v[id * FLUID_AUX_STRIDE].w = uintBitsToFloat(P.phase);
        P.phase = phaseWord;
        pa.p[i] = P;
    }

    if (candidate)
        atomicAdd(sCandidates, 1u);
    barrier();
    if (lid == 0u && gl_WorkGroupID.x < FLUID_K_CHUNKS_MAX)
        candidates.v[FLUID_K_ANCHOR_COUNTS + gl_WorkGroupID.x] = sCandidates;
}
