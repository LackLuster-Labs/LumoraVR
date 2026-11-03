// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The frame's commands, three passes on push constant Pass:
//   0  every live slot runs the particle-wide ops in order (remove, clear, fade, wake, anchor drops, velocity)
//   1  spawns: thread t makes the t-th new particle of the frame, appended after the live range with an id popped
//      off the free stack, so the same commands give the same ids and slots on every run
//   2  growth: the ids a grown buffer added go onto the free stack (Arg = how many, Substep = the old capacity)
// Pass 0 and 1 touch disjoint slots and share one compute list. Nothing here writes Free: pass 1 reads it and the
// sort's count pass settles it, so every spawn thread sees the same stack.
//
// What the data side packs (FluidCommand.ToGpu, FluidFramePacker):
//   Emit, Burst, Release  A = point + litres, B = velocity + spread, C = (tint bits or 0 for the substance's, origin,
//                         substance within the system, nozzle radius), Count = particles
//   Reseed                one ledger cell: A = centre + litres, B = half size, C as an emit, Count = particles; the
//                         cell fills from its bottom layer up, the way settled fluid lies in it
//   Remove                A = sphere, Count = origin (255 any), B.x = litres at most (0 all), B.y = drain index + 1
//   Evaporate             Count = origin, A.x = litres at most, or A.y = the share of each particle to take
// A capped remove takes particles first come, first served: the litres are exact, which particles go is not, and
// nothing that feeds back into the simulation depends on that. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

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
FLUID_DECLARE_SYSTEMS
FLUID_DECLARE_COMMANDS
FLUID_DECLARE_COLLIDERS
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_BY_ID
FLUID_DECLARE_FREE_IDS
FLUID_DECLARE_REGIONS
FLUID_DECLARE_STATS
FLUID_DECLARE_PUSH

#define ANY_ORIGIN 255u
#define NANOLITRES_PER_LITRE 1e9
#define MAX_CAPPED_COMMANDS 256u

void dropBackReference(uint partner, uint id, uint slotCount)
{
    for (uint k = 0u; k < slotCount; k++)
    {
        uint at = partner * slotCount + k;
        if (bonds.b[at].partner == id)
        {
            bonds.b[at].partner = FLUID_NONE;
            bonds.b[at].lambda = 0.0;
        }
    }
}

// Kill in place: the sort moves the slot out and frees the id. Partners lose their side of every bond now, before
// the id can come back as somebody else.
void killParticle(uint slot, uint id, inout FluidParticle q)
{
    uint slotCount = hdr.h.bondSlots;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint at = id * slotCount + k;
        uint partner = bonds.b[at].partner;
        if (partner != FLUID_NONE)
            dropBackReference(partner, id, slotCount);
        bonds.b[at].partner = FLUID_NONE;
    }
    anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
    if ((q.phase & FLUID_PARTICLE_REFINED) != 0u)
        atomicAdd(counters.v[FLUID_C_POOL], 0xFFFFFFFFu);
    q.phase = FLUID_PHASE_DEAD;
}

bool targetMatches(FluidAnchor a, uint kind, uint index)
{
    uint ak = a.kindAge & FLUID_ANCHOR_KIND_MASK;
    if (ak == FLUID_ANCHOR_NONE)
        return false;
    if (kind == FLUID_ANCHOR_BONE || kind == FLUID_ANCHOR_SKIN)
        return (ak == FLUID_ANCHOR_BONE || ak == FLUID_ANCHOR_SKIN) && (a.target >> 8) == index;
    return ak == kind && a.target == index;
}

uint regionOf(vec3 x, float regionSize, uint frame)
{
    return fluidHashCell(fluidCellOf(x, 1.0 / regionSize), frame, FLUID_REGION_TABLE - 1u);
}

// A capped command's running total in nL, in the stats words after the drains'. Litres 0 means no cap.
bool takeUnderCap(uint c, float litres, float volume)
{
    if (!(litres > 0.0) || c >= MAX_CAPPED_COMMANDS)
        return true;
    uint cap = uint(min(litres * NANOLITRES_PER_LITRE, 4.0e9));
    uint nl = max(uint(round(volume * 1e12)), 1u);
    uint before = atomicAdd(stats.v[FLUID_S_DRAINS + MAX_CAPPED_COMMANDS + c], nl);
    return before + nl <= cap;
}

void particlePass(uint i)
{
    uint live = counters.v[FLUID_C_LIVE];
    if (i >= live)
        return;
    FluidParticle q = pa.p[i];
    uint phase = fluidPhaseOf(q.phase);
    uint id = idA.v[i];
    uint sys = fluidSystemOf(q.material);
    uint origin = fluidOriginOf(q.material);
    uint frame = fluidFrameOf(q.material);
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(q.material)];
    bool changed = false;

    // A particle that died in last frame's frame end still holds its bonds; let go of them here, where nothing
    // else reads a bond, before the sort frees the id.
    if (phase == FLUID_PHASE_DEAD)
    {
        if ((q.phase & FLUID_PARTICLE_DYING) != 0u)
        {
            q.phase = FLUID_PHASE_DEAD | FLUID_PARTICLE_DYING;
            killParticle(i, id, q);
            pa.p[i].phase = FLUID_PHASE_DEAD;
        }
        return;
    }


    // A strand's snapped mark lives on its label particle and goes once that particle is back deep in the bulk (frame
    // end A reads it from every piece, so only this pass may clear it). The pieces carry their own slack bit by then.
    // Cleared when the label particle merely re-entered the strand phase, it often went the frame after the snap, and
    // the thread snapped again.
    uint ageWord = acc.a[id].strandAge;
    if ((ageWord & FLUID_STRAND_SNAPPED) != 0u)
    {
        bool deep = ((q.phase >> FLUID_NEIGHBOUR_COUNT_SHIFT) & 255u) >= 12u && (q.phase & FLUID_PARTICLE_SLACK) == 0u;
        if ((ageWord & FLUID_STRAND_SNAP_SEEN) == 0u)
            acc.a[id].strandAge = ageWord | FLUID_STRAND_SNAP_SEEN;
        else if (deep)
            acc.a[id].strandAge = ageWord & FLUID_STRAND_AGE_MASK;
    }

    // Resolve the previous table's anchor before any force or sleep decision uses it. -xlinka
    FluidAnchor anchor = anchors.a[id];
    uint anchorKind = anchor.kindAge & FLUID_ANCHOR_KIND_MASK;
    if (anchorKind == FLUID_ANCHOR_STATIC || anchorKind == FLUID_ANCHOR_RIGID)
    {
        uint target = anchor.target;
        if (anchor.triangle != 0u && anchor.triangle != FLUID_NONE
            && (target >= hdr.h.colliders || colliders.c[target].reserved1 != anchor.triangle))
        {
            target = FLUID_NONE;
            for (uint c = 0u; c < hdr.h.colliders; c++)
                if (colliders.c[c].reserved1 == anchor.triangle)
                {
                    target = c;
                    break;
                }
        }
        if (target >= hdr.h.colliders)
        {
            anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
            q.phase &= ~(FLUID_PARTICLE_ASLEEP | FLUID_PARTICLE_ANCHORED);
            changed = true;
        }
        else
        {
            anchors.a[id].target = target;
            if ((colliders.c[target].flags & (FLUID_COLLIDER_DYNAMIC | FLUID_COLLIDER_MOVED)) != 0u)
            {
                anchors.a[id].kindAge = (anchor.kindAge & ~FLUID_ANCHOR_KIND_MASK) | FLUID_ANCHOR_RIGID;
                q.phase &= ~FLUID_PARTICLE_ASLEEP;
                changed = true;
            }
        }
    }

    uint n = hdr.h.commands;
    for (uint c = 0u; c < n && phase != FLUID_PHASE_DEAD; c++)
    {
        FluidGpuCommand cmd = commands.c[c];
        uint op = cmd.op;
        if (op == FLUID_OP_EMIT || op == FLUID_OP_BURST || op == FLUID_OP_RELEASE || op == FLUID_OP_RESEED || op == FLUID_OP_NONE)
            continue;
        bool anySystem = cmd.system == FLUID_NONE;
        bool mine = anySystem || cmd.system == sys;
        if (op == FLUID_OP_REMOVE)
        {
            bool origins = cmd.count >= ANY_ORIGIN || origin == cmd.count;
            if (mine && origins && distance(q.position, cmd.a.xyz) <= cmd.a.w && takeUnderCap(c, cmd.b.x, q.volume))
            {
                // a drain's share in nL; the summary reports it per drain as litres removed this frame
                uint drain = uint(max(cmd.b.y, 0.0));
                if (drain > 0u && drain <= MAX_CAPPED_COMMANDS)
                    atomicAdd(stats.v[FLUID_S_DRAINS + drain - 1u], max(uint(round(q.volume * 1e12)), 1u));
                killParticle(i, id, q);
                phase = FLUID_PHASE_DEAD;
            }
        }
        else if (op == FLUID_OP_CLEAR_ALL)
        {
            if (mine)
            {
                killParticle(i, id, q);
                phase = FLUID_PHASE_DEAD;
            }
        }
        else if (op == FLUID_OP_CLEAR_ORIGIN)
        {
            if (mine && origin == cmd.count)
            {
                killParticle(i, id, q);
                phase = FLUID_PHASE_DEAD;
            }
        }
        else if (op == FLUID_OP_EVAPORATE)
        {
            // A share in A.y is a seeded, even pick across the origin's fluid; litres in A.x take at most that.
            bool origins = cmd.count >= ANY_ORIGIN || origin == cmd.count;
            if (mine && origins && (cmd.a.y > 0.0 ? fluidRandom(id, cmd.seed) < cmd.a.y : takeUnderCap(c, cmd.a.x, q.volume)))
            {
                killParticle(i, id, q);
                phase = FLUID_PHASE_DEAD;
            }
        }
        else if (op == FLUID_OP_FADE_ORIGIN)
        {
            if (mine && origin == cmd.count)
            {
                q.phase |= FLUID_PARTICLE_FADING;
                q.fadeTime = max(cmd.a.x, 0.01);
                changed = true;
            }
        }
        else if (op == FLUID_OP_SETTLE || op == FLUID_OP_ZERO_VELOCITY)
        {
            if (mine)
            {
                q.velocity = vec3(0.0);
                changed = true;
            }
        }
        else if (op == FLUID_OP_REMOVE_PARTICLES)
        {
            uint m = min(cmd.count, 12u);
            for (uint k = 0u; k < m; k++)
            {
                vec4 v = k < 4u ? cmd.a : (k < 8u ? cmd.b : cmd.c);
                if (floatBitsToUint(v[k & 3u]) == id)
                {
                    killParticle(i, id, q);
                    phase = FLUID_PHASE_DEAD;
                    break;
                }
            }
        }
        else if (op == FLUID_OP_WAKE_BOX)
        {
            vec3 d = abs(q.position - cmd.a.xyz);
            if (mine && all(lessThanEqual(d, cmd.b.xyz)))
            {
                q.phase &= ~FLUID_PARTICLE_ASLEEP;
                changed = true;
                atomicOr(regions.r[regionOf(q.position, 4.0 * sub.smoothingRadius, frame)].awake, 2u);
            }
        }
        else if (op == FLUID_OP_DROP_ANCHORS)
        {
            if (targetMatches(anchors.a[id], cmd.count, cmd.seed))
                anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
        }
        else if (op == FLUID_OP_FREEZE_ANCHORS)
        {
            if (targetMatches(anchors.a[id], cmd.count, cmd.seed))
            {
                uint ka = anchors.a[id].kindAge;
                anchors.a[id].kindAge = cmd.c.x > 0.5 ? (ka | FLUID_ANCHOR_FROZEN) : (ka & ~FLUID_ANCHOR_FROZEN);
            }
        }
    }
    if (phase == FLUID_PHASE_DEAD || changed)
        pa.p[i] = q;
}

uint spawnCount(FluidGpuCommand cmd)
{
    uint op = cmd.op;
    return (op == FLUID_OP_EMIT || op == FLUID_OP_BURST || op == FLUID_OP_RELEASE || op == FLUID_OP_RESEED) ? cmd.count : 0u;
}

void basisOf(vec3 n, out vec3 u, out vec3 w)
{
    vec3 a = abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    u = normalize(cross(a, n));
    w = cross(n, u);
}

// Golden-angle spiral: k of m points spread evenly over a unit disc.
vec2 discPoint(uint k, uint m)
{
    float r = sqrt((float(k) + 0.5) / float(max(m, 1u)));
    float a = float(k) * 2.39996323;
    return r * vec2(cos(a), sin(a));
}

vec3 spherePoint(uint k, uint n)
{
    float z = 1.0 - 2.0 * (float(k) + 0.5) / float(max(n, 1u));
    float r = sqrt(max(1.0 - z * z, 0.0));
    float a = float(k) * 2.39996323;
    return vec3(r * cos(a), z, r * sin(a));
}

void initIdState(uint id, uint slotCount)
{
    for (uint k = 0u; k < slotCount; k++)
    {
        uint at = id * slotCount + k;
        bonds.b[at].partner = FLUID_NONE;
        bonds.b[at].restLength = 0.0;
        bonds.b[at].radius = 0.0;
        bonds.b[at].strainFlags = 0u;
        bonds.b[at].lambda = 0.0;
    }
    anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
    anchors.a[id].target = FLUID_NONE;
    anchors.a[id].triangle = FLUID_NONE;
    anchors.a[id].normal = 0u;
    anchors.a[id].point = vec3(0.0);
    anchors.a[id].strength = 0.0;
    acc.a[id].volumeNl = 0;
    acc.a[id].label = id;
    acc.a[id].strandAge = 0u;
    acc.a[id].claim = 0u;
    uint b = id * FLUID_BY_ID_STRIDE;
    byId.v[b + FLUID_ID_LABEL_NEXT] = id;
    byId.v[b + FLUID_ID_SNAP_BEST] = 0u;
    for (uint k = 0u; k < FLUID_ID_PROPOSAL_COUNT; k++)
        byId.v[b + FLUID_ID_PROPOSALS + k] = FLUID_NONE;
    byId.v[b + FLUID_ID_REFINE_CLAIM] = FLUID_NONE;
    byId.v[b + FLUID_ID_REFINE_KEY] = FLUID_NONE;
    aux.v[id * FLUID_AUX_STRIDE] = vec4(0.0);
    aux.v[id * FLUID_AUX_STRIDE + 1u] = vec4(0.0);
}

void spawnPass(uint t)
{
    uint total = pc.c.arg;
    uint live = counters.v[FLUID_C_LIVE];
    uint free = counters.v[FLUID_C_FREE] + counters.v[FLUID_C_GROW];
    uint made = min(total, free);
    if (t == 0u)
    {
        uint presort = live + made;
        counters.v[FLUID_C_SPAWNED] = made;
        counters.v[FLUID_C_PRESORT] = presort;
        counters.v[FLUID_C_FRAME] += 1u;
        if (total > made)
            counters.v[FLUID_C_SPAWN_OVERFLOW] += total - made;
    }
    if (t >= made)
        return;

    uint n = hdr.h.commands;
    uint base = 0u;
    uint c = 0u;
    for (; c < n; c++)
    {
        uint m = spawnCount(commands.c[c]);
        if (t < base + m)
            break;
        base += m;
    }
    if (c >= n)
        return;
    FluidGpuCommand cmd = commands.c[c];
    uint k = t - base;
    uint slot = live + t;
    uint id = freeIds.v[free - 1u - t];

    uint sys = min(cmd.system, FLUID_MAX_SYSTEMS - 1u);
    uint frame = systems.s[sys].frame;
    // every spawning op packs C the same way: tint bits (0 = the substance's own), origin, the substance's index
    // within its system, a radius
    uint substance = systems.s[sys].substanceBase + uint(max(cmd.c.z, 0.0));
    uint origin = uint(max(cmd.c.y, 0.0));
    uint tint = floatBitsToUint(cmd.c.x);
    float litres = cmd.a.w;
    vec3 position;
    vec3 velocity;
    substance = min(substance, FLUID_MAX_SUBSTANCES - 1u);
    FluidSubstanceRecord sub = substances.s[substance];
    if (tint == 0u)
        tint = sub.tint;
    float d = sub.spacing;
    float volume = max(litres * 1e-3 / float(max(cmd.count, 1u)), 1e-12);
    vec3 jitter = (vec3(fluidRandom(id, cmd.seed), fluidRandom(id + 1u, cmd.seed), fluidRandom(id + 2u, cmd.seed)) - 0.5) * 0.1 * d;

    if (cmd.op == FLUID_OP_EMIT)
    {
        // A stream: the frame's particles fill disc layers behind the nozzle one spacing apart, so they never
        // start on top of each other and the stream carries them out.
        vec3 v = cmd.b.xyz;
        float speed = length(v);
        vec3 dir = speed > 1e-6 ? v / speed : vec3(0.0, -1.0, 0.0);
        vec3 u, w;
        basisOf(dir, u, w);
        float nozzle = max(cmd.c.w, 0.5 * d);
        uint perLayer = max(1u, uint(floor(FLUID_PI * nozzle * nozzle / (d * d))));
        float outSpeed = speed;
        uint layer = k / perLayer;
        // The frame's layers sit one spacing apart upstream of the nozzle, and the disc slot turns with the
        // command's seed, so a frame that emits a single particle does not land on the last one's spot.
        vec2 dp = discPoint((k + cmd.seed) % perLayer, perLayer) * max(nozzle - 0.5 * d, 0.0);
        position = cmd.a.xyz + u * dp.x + w * dp.y - dir * ((float(layer) + 0.5) * d) + jitter;
        float spread = cmd.b.w * (fluidRandom(id + 3u, cmd.seed) * 2.0 - 1.0);
        float twist = fluidRandom(id + 4u, cmd.seed) * 6.2831853;
        velocity = outSpeed * normalize(dir + spread * (u * cos(twist) + w * sin(twist)));
    }
    else if (cmd.op == FLUID_OP_BURST)
    {
        float radius = max(cmd.c.w, 0.5 * d);
        uint m = max(cmd.count, 1u);
        vec3 s = spherePoint(k, m);
        float r = radius * pow((float(k) + 0.5) / float(m), 1.0 / 3.0);
        position = cmd.a.xyz + s * r + jitter;
        velocity = cmd.b.xyz + s * cmd.b.w;
    }
    else if (cmd.op == FLUID_OP_RESEED)
    {
        // A ledger cell, filled from its bottom: rows one spacing apart, x fastest, then z, then up.
        vec3 half_ = max(cmd.b.xyz, vec3(0.5 * d));
        uint nx = max(1u, uint(floor(2.0 * half_.x / d + 0.5)));
        uint nz = max(1u, uint(floor(2.0 * half_.z / d + 0.5)));
        uint ix = k % nx;
        uint iz = (k / nx) % nz;
        uint iy = k / (nx * nz);
        vec3 lo = cmd.a.xyz - half_;
        position = lo + vec3((float(ix) + 0.5) * 2.0 * half_.x / float(nx), (float(iy) + 0.5) * d, (float(iz) + 0.5) * 2.0 * half_.z / float(nz)) + 0.5 * jitter;
        velocity = vec3(0.0);
    }
    else
    {
        uint m = max(cmd.count, 1u);
        float radius = 0.5 * d * pow(float(m), 1.0 / 3.0);
        vec3 s = spherePoint(k, m);
        position = cmd.a.xyz + s * radius * pow((float(k) + 0.5) / float(m), 1.0 / 3.0) + jitter;
        velocity = cmd.b.xyz;
    }

    FluidParticle q;
    q.position = position;
    q.volume = volume;
    q.previous = position;
    q.mass = volume * sub.density;
    q.velocity = velocity;
    q.wetness = 1.0;
    q.phase = FLUID_PHASE_BULK | FLUID_PARTICLE_NEW;
    q.material = (substance << FLUID_SUBSTANCE_SHIFT) | (sys << FLUID_SYSTEM_SHIFT) | ((frame & FLUID_BYTE_MASK) << FLUID_FRAME_SHIFT) | ((origin & FLUID_BYTE_MASK) << FLUID_ORIGIN_SHIFT);
    q.tint = tint;
    q.region = 0u;
    q.age = 0.0;
    q.bandTime = 0.0;
    q.fadeTime = 0.0;
    q.reserved1 = 0u;
    pa.p[slot] = q;
    idA.v[slot] = id;
    idToSlot.v[id] = slot;
    initIdState(id, hdr.h.bondSlots);
}

void growPass(uint t)
{
    uint grow = pc.c.arg;
    uint oldCapacity = pc.c.substep;
    if (t == 0u)
        counters.v[FLUID_C_GROW] = grow;
    if (t >= grow)
        return;
    uint free = counters.v[FLUID_C_FREE];
    freeIds.v[free + t] = oldCapacity + (grow - 1u - t);
}

void main()
{
    uint t = gl_GlobalInvocationID.x;
    if (pc.c.pass == 0u)
        particlePass(t);
    else if (pc.c.pass == 1u)
        spawnPass(t);
    else
        growPass(t);
}
