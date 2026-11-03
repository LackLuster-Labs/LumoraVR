// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Frame end D, one thread per slot that was live before this frame's refinement:
//   asleep slots  wake when their region stopped being quiet
//   awake slots   let go of the ligaments B merged, snap each thread's worst link (the one whose snap key won B's
//                 atomicMax), split the links C granted
//                 at Live + rank with ids from the top of the free stack, merge refined particles back in the bulk
//                 and neck cross-sections, write this frame's anchor candidates in slot order for the next C, and
//                 fall asleep in a quiet region
//   every slot    the frame's bounds, live count, volume and sphere queries (all a paused frame's pass 1 does)
// Ranks inside a chunk come from a shared-memory prefix over the threads in slot order. A thread writes only single
// fields of its own particle, so a refining neighbour's volume write never collides with it. -xlinka

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
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_SYSTEMS
FLUID_DECLARE_FRAMES
FLUID_DECLARE_COLLIDERS
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_PROXIES
FLUID_DECLARE_BONES
FLUID_DECLARE_CONTACT_INDEX
FLUID_DECLARE_SKINNED
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_BY_ID
FLUID_DECLARE_CANDIDATES
FLUID_DECLARE_FREE_IDS
FLUID_DECLARE_REGIONS
FLUID_DECLARE_QUERIES
FLUID_DECLARE_PUSH

shared uint sAnchorFlag[FLUID_GROUP_SIZE];
shared uint sRefineFlag[FLUID_GROUP_SIZE];

#define MERGE_VOLUME 0.4
// Neck merge: a bulk particle of a bonded kind with fewer density neighbours than this, an axis (two bonds within 35
// degrees of opposite) and bonds across it is in a neck several particles wide.
#define NECK_NEIGHBOURS 8.0
#define NECK_AXIS_COS 0.819
// a bond counts as across the axis under this cosine
#define NECK_RUNG_COS 0.6
// a skin anchor takes the nearest contact triangle of the touched part within this many spacings of the particle;
// a part with none that close (a proxy bigger than the skin) keeps a bone anchor
#define SKIN_REACH 2.0

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

void minBits(uint at, float v)
{
    atomicMax(stats.v[at], ~fluidOrderedBits(v));
}

void maxBits(uint at, float v)
{
    atomicMax(stats.v[at], fluidOrderedBits(v));
}

// The phase frame end A gave a particle this frame, from where A left it for B (aux's first word): D writes dying
// phases into the particles themselves, so reading those would race. An asleep particle reads as not in a neck.
uint committedPhase(uint pid, uint ps, uint awake)
{
    return ps < awake ? floatBitsToUint(aux.v[pid * FLUID_AUX_STRIDE].w) : FLUID_PHASE_DEAD;
}

bool refinementClaimed(uint id)
{
    return floatBitsToUint(aux.v[id * FLUID_AUX_STRIDE + 1u].w) != FLUID_NONE;
}

// Original-row flags stay stable in D; B clears empty rows. A snap delays reclamation one frame. -xlinka
bool retainsStrand(uint id, uint slotCount)
{
    for (uint k = 0u; k < slotCount; k++)
        if ((bonds.b[id * slotCount + k].strainFlags & FLUID_BOND_STRAND) != 0u)
            return true;
    return false;
}

// A neck particle's axis: the most nearly opposite pair of its live bonds. False when it is not in a neck.
bool neckAxis(uint id, vec3 x, uint slotCount, uint awake, out vec3 axis, out uint ka, out uint kb)
{
    axis = vec3(0.0);
    ka = FLUID_NONE;
    kb = FLUID_NONE;
    if (refinementClaimed(id) || retainsStrand(id, slotCount))
        return false;
    uint bondCount = 0u;
    float best = 2.0;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint pk = bonds.b[id * slotCount + k].partner;
        // thread links are cleared by snaps and merges in this pass, so a neck never reads them
        if ((bonds.b[id * slotCount + k].strainFlags & FLUID_BOND_STRAND) != 0u)
            continue;
        uint sk = pk != FLUID_NONE ? idToSlot.v[pk] : FLUID_NONE;
        if (sk == FLUID_NONE || sk >= awake)
            continue;
        bondCount++;
        vec3 dk = normalize(pa.p[sk].position - x + vec3(1e-12));
        for (uint m = k + 1u; m < slotCount; m++)
        {
            uint pm = bonds.b[id * slotCount + m].partner;
            if ((bonds.b[id * slotCount + m].strainFlags & FLUID_BOND_STRAND) != 0u)
                continue;
            uint sm = pm != FLUID_NONE ? idToSlot.v[pm] : FLUID_NONE;
            if (sm == FLUID_NONE || sm >= awake)
                continue;
            float dd = dot(dk, normalize(pa.p[sm].position - x + vec3(1e-12)));
            if (dd < best)
            {
                best = dd;
                ka = k;
                kb = m;
                axis = dk;
            }
        }
    }
    if (bondCount <= 2u || best >= -NECK_AXIS_COS)
        return false;

    // A sheet has only one transverse direction. Preserve it until a neck has a real cross-section. -xlinka
    vec3 reference = abs(axis.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 u = normalize(cross(axis, reference));
    vec3 v = cross(axis, u);
    vec3 transverse = vec3(0.0);
    for (uint k = 0u; k < slotCount; k++)
    {
        FluidBond bond = bonds.b[id * slotCount + k];
        if (bond.partner == FLUID_NONE || (bond.strainFlags & FLUID_BOND_STRAND) != 0u)
            continue;
        uint sk = idToSlot.v[bond.partner];
        if (sk == FLUID_NONE || sk >= awake)
            continue;
        vec3 delta = pa.p[sk].position - x;
        vec2 q = vec2(dot(delta, u), dot(delta, v));
        transverse += vec3(q.x * q.x, q.x * q.y, q.y * q.y);
    }
    float trace = transverse.x + transverse.z;
    float determinant = transverse.x * transverse.z - transverse.y * transverse.y;
    if (trace <= 1e-12 || determinant <= 0.1 * trace * trace)
        return false;

    // A bent sheet can span both transverse axes while all its liquid lies on one side. -xlinka
    // Only coalesce a neck whose rungs surround the axis, not the edge of an open film. -xlinka
    uint rungs = 0u;
    for (uint k = 0u; k < slotCount; k++)
    {
        FluidBond first = bonds.b[id * slotCount + k];
        if (k == ka || k == kb || first.partner == FLUID_NONE || (first.strainFlags & FLUID_BOND_STRAND) != 0u)
            continue;
        uint sk = idToSlot.v[first.partner];
        if (sk == FLUID_NONE || sk >= awake)
            continue;
        vec3 dk = pa.p[sk].position - x;
        if (abs(dot(normalize(dk + vec3(1e-12)), axis)) >= NECK_RUNG_COS)
            continue;
        vec2 q = vec2(dot(dk, u), dot(dk, v));
        rungs++;
        bool left = false, right = false;
        for (uint m = 0u; m < slotCount; m++)
        {
            FluidBond second = bonds.b[id * slotCount + m];
            if (m == ka || m == kb || second.partner == FLUID_NONE || (second.strainFlags & FLUID_BOND_STRAND) != 0u)
                continue;
            uint sm = idToSlot.v[second.partner];
            if (sm == FLUID_NONE || sm >= awake)
                continue;
            vec3 dm = pa.p[sm].position - x;
            if (abs(dot(normalize(dm + vec3(1e-12)), axis)) >= NECK_RUNG_COS)
                continue;
            vec2 r = vec2(dot(dm, u), dot(dm, v));
            float side = q.x * r.y - q.y * r.x;
            float tolerance = 1e-4 * length(q) * length(r);
            left = left || side > tolerance;
            right = right || side < -tolerance;
        }
        if (!left || !right)
            return false;
    }
    return rungs >= 3u;
}

// Whether a particle is in a neck with a lower-id partner across its axis (it gives its volume away). GLSL has no
// recursion, so the receiver test below calls this, not neckRole.
bool neckIsGiver(uint id, uint phaseWord, vec3 x, uint slotCount, uint awake)
{
    if (fluidPhaseOf(phaseWord) != FLUID_PHASE_BULK || (phaseWord & FLUID_PARTICLE_RESTING) != 0u
        || float((phaseWord >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u) >= NECK_NEIGHBOURS
        || (anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE)
        return false;
    vec3 axis;
    uint ka, kb;
    if (!neckAxis(id, x, slotCount, awake, axis, ka, kb))
        return false;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint pk = bonds.b[id * slotCount + k].partner;
        if (k == ka || k == kb || pk == FLUID_NONE || pk >= id || (bonds.b[id * slotCount + k].strainFlags & FLUID_BOND_STRAND) != 0u)
            continue;
        uint sk = idToSlot.v[pk];
        if (sk == FLUID_NONE || sk >= awake || refinementClaimed(pk))
            continue;
        if (abs(dot(normalize(pa.p[sk].position - x + vec3(1e-12)), axis)) < NECK_RUNG_COS)
            return true;
    }
    return false;
}

// A particle that can take volume in this pass: one that does not die here. A refined particle back in the bulk merges
// away, and a neck giver hands itself to its partner, so neither receives; a neck particle that does not give does.
bool stableReceiver(uint pid, uint ps, uint awake)
{
    uint w = committedPhase(pid, ps, awake);
    uint ph = fluidPhaseOf(w);
    if (refinementClaimed(pid) || ph == FLUID_PHASE_DEAD || ((w & FLUID_PARTICLE_REFINED) != 0u && ph == FLUID_PHASE_BULK))
        return false;
    return !neckIsGiver(pid, w, pa.p[ps].position, hdr.h.bondSlots, awake);
}

// Coarsening can remove a redundant node only when its receiver already joins every surviving neighbour.
// The receiver is unclaimed and these witness bonds are not strands, so D cannot split or snap them. -xlinka
bool preservesConnectivity(uint id, uint into, uint slotCount, uint awake)
{
    for (uint k = 0u; k < slotCount; k++)
    {
        uint j = bonds.b[id * slotCount + k].partner;
        if (j == FLUID_NONE || j == into)
            continue;
        uint js = idToSlot.v[j];
        if (js == FLUID_NONE)
            continue;
        uint phaseWord = js < awake ? committedPhase(j, js, awake) : pa.p[js].phase;
        if (fluidPhaseOf(phaseWord) == FLUID_PHASE_DEAD)
            continue;
        // A reciprocal strand row can release the witness even if this end has already become bulk. -xlinka
        if (retainsStrand(j, slotCount))
            return false;
        bool joined = false;
        for (uint m = 0u; m < slotCount; m++)
        {
            FluidBond witness = bonds.b[into * slotCount + m];
            if (witness.partner == j && (witness.strainFlags & FLUID_BOND_STRAND) == 0u)
            {
                joined = true;
                break;
            }
        }
        if (!joined)
            return false;
    }
    return true;
}

// Whether a particle is in a neck and has a lower-id partner across its axis, so gives its volume away. With
// wantTarget, into is the lowest such partner that does not give itself (FLUID_NONE when none receives yet).
bool neckRole(uint id, uint phaseWord, vec3 x, uint slotCount, uint awake, bool wantTarget, out uint into)
{
    into = FLUID_NONE;
    if (fluidPhaseOf(phaseWord) != FLUID_PHASE_BULK || (phaseWord & FLUID_PARTICLE_RESTING) != 0u
        || float((phaseWord >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u) >= NECK_NEIGHBOURS
        || (anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE)
        return false;
    vec3 axis;
    uint ka, kb;
    if (!neckAxis(id, x, slotCount, awake, axis, ka, kb))
        return false;
    bool giver = false;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint pk = bonds.b[id * slotCount + k].partner;
        if (k == ka || k == kb || pk == FLUID_NONE || pk >= id || (bonds.b[id * slotCount + k].strainFlags & FLUID_BOND_STRAND) != 0u)
            continue;
        uint sk = idToSlot.v[pk];
        if (sk == FLUID_NONE || sk >= awake || refinementClaimed(pk))
            continue;
        if (abs(dot(normalize(pa.p[sk].position - x + vec3(1e-12)), axis)) >= NECK_RUNG_COS)
            continue;
        giver = true;
        if (!wantTarget || pk >= into)
            continue;
        if (stableReceiver(pk, sk, awake))
            into = pk;
    }
    return giver;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    uint liveBefore = counters.v[FLUID_C_REFINE_BASE];
    uint awake = counters.v[FLUID_C_AWAKE];
    bool here = i < liveBefore;
    bool isAwake = i < awake;

    FluidParticle P = pa.p[here ? i : 0u];
    uint id = idA.v[here ? i : 0u];
    uint phase = fluidPhaseOf(P.phase);
    bool alive = here && phase != FLUID_PHASE_DEAD;
    uint b = id * FLUID_BY_ID_STRIDE;
    uint refineKey = alive && isAwake ? byId.v[b + FLUID_ID_REFINE_KEY] : FLUID_NONE;
    bool anchorCandidate = alive && isAwake && acc.a[id].claim != 0u;
    sAnchorFlag[lid] = anchorCandidate ? 1u : 0u;
    sRefineFlag[lid] = refineKey != FLUID_NONE ? 1u : 0u;
    barrier();
    uint anchorRank = 0u;
    uint refineRank = 0u;
    for (uint l = 0u; l < lid; l++)
    {
        anchorRank += sAnchorFlag[l];
        refineRank += sRefineFlag[l];
    }
    uint chunk = gl_WorkGroupID.x;
    if (!alive)
        return;

    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float d = sub.spacing;
    uint slotCount = hdr.h.bondSlots;
    uint sys = min(fluidSystemOf(P.material), FLUID_MAX_SYSTEMS - 1u);
    uint f = fluidFrameOf(P.material);
    bool inFrame = f != 0u && f < hdr.h.frames;
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, 1.0, fq, fpos, fscale);
    vec3 xw = inFrame ? fluidToWorld(P.position, fq, fpos, fscale) : P.position;

    // Every live slot: bounds, count, volume, queries.
    uint sbase = FLUID_S_SYSTEM + sys * FLUID_S_SYSTEM_STRIDE;
    minBits(sbase + FLUID_S_SYS_MIN, xw.x);
    minBits(sbase + FLUID_S_SYS_MIN + 1u, xw.y);
    minBits(sbase + FLUID_S_SYS_MIN + 2u, xw.z);
    maxBits(sbase + FLUID_S_SYS_MAX, xw.x);
    maxBits(sbase + FLUID_S_SYS_MAX + 1u, xw.y);
    maxBits(sbase + FLUID_S_SYS_MAX + 2u, xw.z);
    atomicAdd(stats.v[sbase + FLUID_S_SYS_LIVE], 1u);
    atomicAdd(stats.v[sbase + FLUID_S_SYS_MICROLITRES], uint(round(P.volume * 1e9)));
    uint queryCount = min(hdr.h.queries, uint(FLUID_SUMMARY_QUERIES_CAPACITY));
    for (uint q = 0u; q < queryCount; q++)
    {
        FluidQuerySphere Q = queries.q[q];
        if (distance(xw, Q.center) <= Q.radius)
        {
            atomicAdd(stats.v[FLUID_S_QUERIES + q * 2u], uint(round(P.volume * 1e9)));
            if (length(P.velocity) > 0.05)
                atomicAdd(stats.v[FLUID_S_QUERIES + q * 2u + 1u], 1u);
        }
    }

    // Pass 1, a frame with no substeps (a paused world): the counts above are all the summary needs.
    if (pc.c.pass == 1u)
        return;

    float regionSize = 4.0 * sub.smoothingRadius;
    uint region = fluidHashCell(fluidCellOf(P.position, 1.0 / regionSize), f, FLUID_REGION_TABLE - 1u);
    bool quiet = uintBitsToFloat(regions.r[region].quietFrames) >= FLUID_SLEEP_SECONDS;
    if (!isAwake)
    {
        if (!quiet)
            pa.p[i].phase = P.phase & ~FLUID_PARTICLE_ASLEEP;
        byId.v[b + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
        return;
    }

    // Snaps: each owned link whose snap key won its thread's pick breaks at both ends, and its stored stretch comes
    // back as recoil on its own, with nothing holding the ends together any more. A link belongs to the thread label
    // of its carrier end (frame end B's pick); a ligament from root to root, with no carrier, snaps on its own.
    uint label = acc.a[id].label;
    {
        for (uint k = 0u; k < slotCount; k++)
        {
            uint at = id * slotCount + k;
            FluidBond bnd = bonds.b[at];
            uint j = bnd.partner;
            if (j == FLUID_NONE || (bnd.strainFlags & (FLUID_BOND_STRAND | FLUID_BOND_OWNER)) != (FLUID_BOND_STRAND | FLUID_BOND_OWNER))
                continue;
            uint js = idToSlot.v[j];
            if (js == FLUID_NONE)
                continue;
            if ((bnd.strainFlags & FLUID_BOND_MERGED) != 0u)
            {
                // coalesced into a parallel ligament (frame end B): let go at both ends, no snap
                bonds.b[at].partner = FLUID_NONE;
                uint back = backSlot(j, id, slotCount);
                if (back != FLUID_NONE)
                    bonds.b[back].partner = FLUID_NONE;
                continue;
            }
            uint myWord = committedPhase(id, i, awake);
            uint jWord = js < awake ? committedPhase(j, js, awake) : pa.p[js].phase;
            bool meCarrier = fluidCarrier(myWord);
            bool heCarrier = fluidCarrier(jWord);
            uint owner = meCarrier ? label : acc.a[j].label;
            bool slackPiece = ((myWord | jWord) & FLUID_PARTICLE_SLACK) != 0u;
            float s = slackPiece ? sub.minThread / max(bnd.radius, 1e-9) : linkBadness(bnd.radius, bnd.strainFlags, sub);
            if (s < 1.0)
                continue;
            // A short, nondepleted strand at rest in goo relaxes into it. Exposed or exhausted necks still part. -xlinka
            if (!slackPiece && bnd.radius >= sub.minThread && distance(P.position, pa.p[js].position) <= 1.5 * d
                && !meCarrier && !heCarrier && (phase == FLUID_PHASE_STRAND || fluidPhaseOf(jWord) == FLUID_PHASE_STRAND
                || ((P.phase | jWord) & FLUID_PARTICLE_REFINED) != 0u))
                continue;
            // Historical labels span disconnected pieces: every depleted slack neck releases on its own. -xlinka
            if (!slackPiece && (meCarrier || heCarrier))
            {
                uint best = atomicAdd(byId.v[owner * FLUID_BY_ID_STRIDE + FLUID_ID_SNAP_BEST], 0u);
                if (best == 0u || fluidSnapKey(s, id, j) != best)
                    continue;
                // Consume this frame's winning key separately from the historical recoil mark. -xlinka
                if (atomicCompSwap(byId.v[owner * FLUID_BY_ID_STRIDE + FLUID_ID_SNAP_BEST], best, 0u) != best)
                    continue;
                atomicOr(acc.a[owner].strandAge, FLUID_STRAND_SNAPPED);
            }
            bonds.b[at].partner = FLUID_NONE;
            uint back = backSlot(j, id, slotCount);
            if (back != FLUID_NONE)
                bonds.b[back].partner = FLUID_NONE;
            atomicAdd(stats.v[sbase + FLUID_S_SYS_SNAPS], 1u);
        }
    }

    // Refinement: a midpoint particle on the granted link, a quarter of each end's volume.
    if (refineKey != FLUID_NONE && chunk < FLUID_K_CHUNKS_MAX)
    {
        uint rank = candidates.v[FLUID_K_REFINE_SCAN + chunk] + refineRank;
        if (rank < counters.v[FLUID_C_REFINE_ACCEPTED])
        {
            uint k = refineKey & 15u;
            uint at = id * slotCount + k;
            FluidBond bnd = bonds.b[at];
            uint j = bnd.partner;
            uint js = j != FLUID_NONE ? idToSlot.v[j] : FLUID_NONE;
            if (js != FLUID_NONE)
            {
                FluidParticle Q = pa.p[js];
                uint newSlot = liveBefore + rank;
                uint newId = freeIds.v[counters.v[FLUID_C_REFINE_FREE_TOP] - 1u - rank];
                float giveI = 0.25 * P.volume;
                float giveJ = 0.25 * Q.volume;
                P.volume -= giveI;
                pa.p[i].volume = P.volume;
                pa.p[i].mass = P.volume * sub.density;
                pa.p[js].volume = Q.volume - giveJ;
                pa.p[js].mass = (Q.volume - giveJ) * sub.density;

                FluidParticle M;
                M.position = 0.5 * (P.position + Q.position);
                M.volume = giveI + giveJ;
                M.previous = M.position;
                M.mass = M.volume * sub.density;
                // The new node receives the momentum of the liquid each end actually donates. -xlinka
                M.velocity = (P.velocity * giveI + Q.velocity * giveJ) / max(M.volume, 1e-20);
                M.wetness = 0.5 * (P.wetness + Q.wetness);
                M.phase = FLUID_PHASE_STRAND | FLUID_PARTICLE_NEW | FLUID_PARTICLE_REFINED
                    | ((P.phase | Q.phase) & FLUID_PARTICLE_SLACK);
                M.material = P.material;
                M.tint = P.tint;
                M.region = 0u;
                M.age = min(P.age, Q.age);
                M.bandTime = 0.0;
                M.fadeTime = 0.0;
                M.reserved1 = 0u;
                pa.p[newSlot] = M;
                idA.v[newSlot] = newId;
                idToSlot.v[newId] = newSlot;

                float halfRest = 0.5 * bnd.restLength;
                bonds.b[at].partner = newId;
                bonds.b[at].restLength = halfRest;
                bonds.b[at].lambda = 0.0;
                uint back = backSlot(j, id, slotCount);
                if (back != FLUID_NONE)
                {
                    bonds.b[back].partner = newId;
                    bonds.b[back].restLength = halfRest;
                    bonds.b[back].lambda = 0.0;
                }
                for (uint m = 0u; m < slotCount; m++)
                {
                    uint nat = newId * slotCount + m;
                    bonds.b[nat].partner = m == 0u ? id : (m == 1u ? j : FLUID_NONE);
                    bonds.b[nat].restLength = halfRest;
                    bonds.b[nat].radius = bnd.radius;
                    bonds.b[nat].strainFlags = bnd.strainFlags;
                    bonds.b[nat].lambda = 0.0;
                }
                anchors.a[newId].kindAge = FLUID_ANCHOR_NONE;
                anchors.a[newId].target = FLUID_NONE;
                anchors.a[newId].triangle = FLUID_NONE;
                anchors.a[newId].normal = 0u;
                anchors.a[newId].point = vec3(0.0);
                anchors.a[newId].strength = 0.0;
                acc.a[newId].volumeNl = 0;
                // A midpoint joins the label its strand already has, and starts one of its own only on a link between
                // two bulk ends. Taking the smaller of the two, a fresh id from the free stack often undercut the
                // strand's label, the thread then carried two labels while they converged, and a snap marked only one.
                bool jStrand = fluidPhaseOf(committedPhase(j, js, awake)) == FLUID_PHASE_STRAND;
                acc.a[newId].label = phase == FLUID_PHASE_STRAND ? (jStrand ? min(acc.a[id].label, acc.a[j].label) : acc.a[id].label)
                    : (jStrand ? acc.a[j].label : newId);
                acc.a[newId].strandAge = min(acc.a[id].strandAge & FLUID_STRAND_AGE_MASK, acc.a[j].strandAge & FLUID_STRAND_AGE_MASK)
                    | ((M.phase & FLUID_PARTICLE_SLACK) != 0u ? FLUID_STRAND_SNAPPED : 0u);
                acc.a[newId].claim = 0u;
                uint nb = newId * FLUID_BY_ID_STRIDE;
                byId.v[nb + FLUID_ID_LABEL_NEXT] = acc.a[newId].label;
                byId.v[nb + FLUID_ID_SNAP_BEST] = 0u;
                for (uint q = 0u; q < FLUID_ID_PROPOSAL_COUNT; q++)
                    byId.v[nb + FLUID_ID_PROPOSALS + q] = FLUID_NONE;
                byId.v[nb + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
                byId.v[nb + FLUID_ID_REFINE_KEY] = FLUID_NONE;
                aux.v[newId * FLUID_AUX_STRIDE] = 0.5 * (aux.v[id * FLUID_AUX_STRIDE] + aux.v[j * FLUID_AUX_STRIDE]);
                aux.v[newId * FLUID_AUX_STRIDE + 1u] = vec4(0.0, 0.0, 0.0, uintBitsToFloat(FLUID_NONE));
            }
        }
    }

    bool linkedRefinement = (P.phase & FLUID_PARTICLE_REFINED) != 0u && retainsStrand(id, slotCount);

    // Merge: a small particle refinement made, back in the bulk and not on any link being split, gives its volume
    // to its nearest big bonded neighbour (applied by next frame's B) and dies.
    bool claimed = refinementClaimed(id);
    if (!claimed && !linkedRefinement && refineKey == FLUID_NONE && phase == FLUID_PHASE_BULK && (P.phase & FLUID_PARTICLE_REFINED) != 0u
        && (P.volume < MERGE_VOLUME * d * d * d || ((P.phase >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u) >= FLUID_CARRIER_NEIGHBOURS))
    {
        uint into = FLUID_NONE;
        float nearest = 1e30;
        for (uint k = 0u; k < slotCount; k++)
        {
            uint j = bonds.b[id * slotCount + k].partner;
            if (j == FLUID_NONE)
                continue;
            uint js = idToSlot.v[j];
            if (js == FLUID_NONE || js >= awake || !stableReceiver(j, js, awake) || pa.p[js].volume < MERGE_VOLUME * d * d * d)
                continue;
            if (!preservesConnectivity(id, j, slotCount, awake))
                continue;
            float dd = distance(P.position, pa.p[js].position);
            if (dd < nearest)
            {
                nearest = dd;
                into = j;
            }
        }
        // none bonded that is big enough (a whole fallen piece of thread): the nearest bulk particle around it that
        // refinement never made, read from what is stable in this pass (committed phases)
        uint count = floatBitsToUint(slots.v[i * FLUID_SLOT_STRIDE].z);
        for (uint e = 0u; into == FLUID_NONE && e < count; e++)
        {
            uint ks = lists.v[i * FLUID_MAX_NEIGHBOURS + e];
            if (ks >= awake)
                continue;
            uint kid = idA.v[ks];
            uint kw = committedPhase(kid, ks, awake);
            if (fluidPhaseOf(kw) != FLUID_PHASE_BULK || !stableReceiver(kid, ks, awake)
                || ((kw >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u) < FLUID_CARRIER_NEIGHBOURS)
                continue;
            if (!preservesConnectivity(id, kid, slotCount, awake))
                continue;
            float dd = distance(P.position, pa.p[ks].position);
            if (dd < nearest)
            {
                nearest = dd;
                into = kid;
            }
        }
        if (into != FLUID_NONE)
        {
            atomicAdd(acc.a[into].volumeNl, fluidToNanolitres(P.volume));
            atomicAdd(counters.v[FLUID_C_POOL], 0xFFFFFFFFu);
            pa.p[i].phase = FLUID_PHASE_DEAD | FLUID_PARTICLE_DYING;
            byId.v[b + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
            acc.a[id].claim = 0u;
            return;
        }
    }

    // Neck merge: a neck several particles wide gives its cross-section to one chain, so it thins and snaps as one
    // thread. A giver hands its volume to its lowest-id partner across the axis that does not give itself (applied by
    // next frame's B) and dies; the receiver keeps its own bonds along the axis. Releasing the rungs instead split
    // every connected neck into parallel chains. Distinct held contacts keep their own particles. Only reads
    // here: B's stable claim snapshot excludes endpoints whose bonds refinement rewrites. -xlinka
    if (!claimed && refineKey == FLUID_NONE && phase == FLUID_PHASE_BULK && sub.bondCompliance > 0.0
        && (anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) == FLUID_ANCHOR_NONE)
    {
        uint into = FLUID_NONE;
        if (neckRole(id, committedPhase(id, i, awake), P.position, slotCount, awake, true, into) && into != FLUID_NONE)
        {
            atomicAdd(acc.a[into].volumeNl, fluidToNanolitres(P.volume));
            if ((P.phase & FLUID_PARTICLE_REFINED) != 0u)
                atomicAdd(counters.v[FLUID_C_POOL], 0xFFFFFFFFu);
            pa.p[i].phase = FLUID_PHASE_DEAD | FLUID_PARTICLE_DYING;
            byId.v[b + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
            acc.a[id].claim = 0u;
            return;
        }
    }

    // This frame's anchor candidates, in slot order, for the next C to accept per target.
    if (anchorCandidate && chunk < FLUID_K_CHUNKS_MAX)
    {
        uint rank = candidates.v[FLUID_K_ANCHOR_SCAN + chunk] + anchorRank;
        uint sb = i * FLUID_SLOT_STRIDE;
        uint key = floatBitsToUint(slots.v[sb + 2u].w);
        uint kind = key >> FLUID_TARGET_KIND_SHIFT;
        uint index = key & FLUID_TARGET_INDEX_MASK;
        vec3 nWorld = inFrame ? fluidQuatRotate(fq, slots.v[sb + 1u].xyz) : slots.v[sb + 1u].xyz;
        if (rank < FLUID_K_LIST_CAP)
        {
            uint e = FLUID_K_LIST + rank * FLUID_K_LIST_STRIDE;
            vec3 local = vec3(0.0);
            vec3 nLocal = vec3(0.0, 1.0, 0.0);
            uint target = index;
            uint triangle = FLUID_NONE;
            bool ok = true;
            if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
            {
                if (index < hdr.h.colliders)
                {
                    FluidColliderRecord C = colliders.c[index];
                    triangle = C.reserved1;
                    local = fluidToLocal(xw, C.rotation, C.position, C.scale);
                    nLocal = normalize(fluidQuatInverseRotate(C.rotation, nWorld));
                }
                else
                    ok = false;
            }
            else if (kind == FLUID_ANCHOR_BONE)
            {
                // People anchors store the person's slot and part, and a bone or triangle counted from the
                // person's first: the frame's lists are rebuilt every frame.
                FluidProxyRecord X = proxies.p[index];
                uint person = X.person;
                if (person < hdr.h.people && X.bone < hdr.h.bones && X.bone >= people.p[person].boneStart)
                {
                    FluidPersonRecord R = people.p[person];
                    FluidBoneRecord B = bones.b[X.bone];
                    local = fluidAffineInverse(B.row0, B.row1, B.row2, xw);
                    vec3 tip = fluidAffineInverse(B.row0, B.row1, B.row2, xw + nWorld * 0.01);
                    nLocal = length(tip - local) > 1e-9 ? normalize(tip - local) : vec3(0.0, 1.0, 0.0);
                    target = (person << 8) | (X.part & 255u);
                    triangle = X.bone - R.boneStart;

                    // Skin: the nearest contact triangle of this part. Only this frame's few candidates scan, so
                    // a linear walk over the person's triangles costs nothing that shows.
                    float bestD = SKIN_REACH * d;
                    uint bestT = FLUID_NONE;
                    vec3 bestBary = vec3(0.0);
                    uint triEnd = min(R.triangleStart + R.triangleCount, hdr.h.contactTriangles);
                    for (uint tri = R.triangleStart; tri < triEnd; tri++)
                    {
                        uint i0 = contactIndex.v[tri * 3u];
                        if (skinned.v[i0].region != (X.part & 255u))
                            continue;
                        vec3 a = skinned.v[i0].position;
                        vec3 bb = skinned.v[contactIndex.v[tri * 3u + 1u]].position;
                        vec3 c = skinned.v[contactIndex.v[tri * 3u + 2u]].position;
                        vec3 q = fluidClosestOnTriangle(xw, a, bb, c);
                        float dq = distance(xw, q);
                        if (dq >= bestD)
                            continue;
                        vec3 n = cross(bb - a, c - a);
                        float nl = length(n);
                        if (nl < 1e-12)
                            continue;
                        n /= nl;
                        // barycentric of q, then the particle's own offset along the triangle's normal, so the
                        // anchor holds it where it sits and never yanks it onto the skin
                        vec3 v0 = bb - a, v1 = c - a, v2 = q - a;
                        float d00 = dot(v0, v0), d01 = dot(v0, v1), d11 = dot(v1, v1), d20 = dot(v2, v0), d21 = dot(v2, v1);
                        float den = d00 * d11 - d01 * d01;
                        if (abs(den) < 1e-20)
                            continue;
                        float u = (d11 * d20 - d01 * d21) / den;
                        float w = (d00 * d21 - d01 * d20) / den;
                        bestD = dq;
                        bestT = tri;
                        bestBary = vec3(u, w, clamp(dot(xw - q, n), -0.5 * d, 1.5 * d));
                    }
                    if (bestT != FLUID_NONE)
                    {
                        kind = FLUID_ANCHOR_SKIN;
                        triangle = bestT - R.triangleStart;
                        local = bestBary;
                    }
                }
                else
                    ok = false;
            }
            else if (kind == FLUID_ANCHOR_SKIN)
            {
                // The skin collision's contact (FluidSkinCollide): index is the frame's contact triangle. Its person is
                // the one whose range holds it; the anchor keeps the closest point's barycentric and the particle's own
                // offset along the triangle's normal, never less than its radius: the skin collision keeps it outside,
                // and an anchor holding it half a spacing under the skin would pull it back in.
                uint tri = index;
                uint person = FLUID_NONE;
                uint people_ = min(hdr.h.people, uint(FLUID_S_PEOPLE_MAX));
                for (uint pr = 0u; pr < people_ && person == FLUID_NONE; pr++)
                {
                    if (tri >= people.p[pr].triangleStart && tri < people.p[pr].triangleStart + people.p[pr].triangleCount)
                        person = pr;
                }
                ok = false;
                if (person != FLUID_NONE && tri < hdr.h.contactTriangles)
                {
                    FluidPersonRecord R = people.p[person];
                    uint i0 = contactIndex.v[tri * 3u];
                    vec3 a = skinned.v[i0].position;
                    vec3 bb = skinned.v[contactIndex.v[tri * 3u + 1u]].position;
                    vec3 c = skinned.v[contactIndex.v[tri * 3u + 2u]].position;
                    vec3 q = fluidClosestOnTriangle(xw, a, bb, c);
                    vec3 n = cross(bb - a, c - a);
                    float nl = length(n);
                    vec3 v0 = bb - a, v1 = c - a, v2 = q - a;
                    float d00 = dot(v0, v0), d01 = dot(v0, v1), d11 = dot(v1, v1), d20 = dot(v2, v0), d21 = dot(v2, v1);
                    float den = d00 * d11 - d01 * d01;
                    if (nl > 1e-12 && abs(den) > 1e-20)
                    {
                        n /= nl;
                        target = (person << 8) | (skinned.v[i0].region & 255u);
                        triangle = tri - R.triangleStart;
                        local = vec3((d11 * d20 - d01 * d21) / den, (d00 * d21 - d01 * d20) / den, clamp(dot(xw - q, n), 0.5 * d, 1.5 * d));
                        nLocal = n;
                        ok = true;
                    }
                }
            }
            else
                ok = false;
            candidates.v[e] = ok ? id : FLUID_NONE;
            candidates.v[e + 1u] = kind;
            candidates.v[e + 2u] = target;
            candidates.v[e + 3u] = triangle;
            candidates.v[e + 4u] = fluidPackNormal(nLocal);
            candidates.v[e + 5u] = floatBitsToUint(local.x);
            candidates.v[e + 6u] = floatBitsToUint(local.y);
            candidates.v[e + 7u] = floatBitsToUint(local.z);
        }
    }

    // Sleep. Particles held by anything that moves never sleep.
    uint akind = anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK;
    bool held = akind != FLUID_ANCHOR_NONE && akind != FLUID_ANCHOR_STATIC;
    if (quiet && !held && phase != FLUID_PHASE_STRAND)
        pa.p[i].phase = P.phase | FLUID_PARTICLE_ASLEEP;

    byId.v[b + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
    acc.a[id].claim = 0u;
}
