// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Fluid against the skinned bodies of the people near it: the whole contact mesh (head, hair, face, torso, arms,
// hands, legs, feet), skinned this frame by FluidSkin. Recorded through the solver's after-collide hook, so it sees
// predict's B positions and contact slots each substep and leaves them the way predict would have.
//
// Once a frame (first substep):
//   pass 0  mark     a thread per awake particle: the coarse cells along where it can get to this frame (its own
//                    velocity), as bits, and every person whose bone box that path reaches
//   pass 1  insert   a thread per contact triangle of a person a particle reached: a triangle near no marked cell
//                    stops; the rest go into a compact list, with the box of both of the frame's poses, and count the
//                    fine cells within the list reach of the triangle (a cell-to-triangle distance, not its box)
//   pass 4  scan     a workgroup per 64 cells: each cell's start within its block, and the block's total
//   pass 3  blocks   one workgroup: each block's start (a cell's start is its block's plus its own)
//   pass 5  scatter  a thread per kept triangle: its index into every cell it counted
// Every substep:
//   pass 2  collide  FLUID_LANES threads per awake particle split each cell's list and reduce through shared memory:
//                    the cells along the substep's path give the triangles it can have crossed (it goes back to the
//                    side it came from); the cell it ends in gives the nearest, which pushes it out along its skinned
//                    normal and becomes its contact when nearer than what predict found; a pushed particle gets two
//                    more rounds against whatever it is still inside (creases)
//
// Speed costs only the fast particles: a triangle's cells take in the list reach alone (radius, band and half a
// spacing of skin), a particle walks the cells its own path crosses, and one that moved less than its radius against
// the skin skips the crossing walk (it cannot have gone through). The skin moves inside the frame, so a triangle is
// taken at pose(t) and the particle's start of substep is carried into the triangle's frame at pose(t - 1/n) before
// the crossing test: a hand swung through a stream catches it the same as a stream poured on a still hand.
//
// Closed surfaces (skin, clothes) are one sided with a thickness: a particle just inside one goes out the front. Open
// surfaces (hair cards, hems; FLUID_CONTACT_TWO_SIDED) are two sided and hold a particle on the side it came from.
// The nearest triangle resolves first, so a particle beside a finger never gets pushed through to the far side. What
// a push only undoes of a depth the particle already had at the start of the substep moves its start too, so the
// update kernel (velocity from start to end) never turns a push-out into spray. People's proxies stay predict's deep
// backstop.
//
// Every choice breaks ties by the triangle's index, never by list order, so the atomics in the build cannot make a
// replay differ. -xlinka

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PB
FLUID_DECLARE_SLOTS
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_FRAMES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_BONES
FLUID_DECLARE_CONTACT_VERTS
FLUID_DECLARE_CONTACT_INDEX
FLUID_DECLARE_SKINNED
FLUID_DECLARE_HIDDEN
FLUID_DECLARE_PUSH

// The pass's own buffers, after the solver's table (FluidSkinCollidePass carries the same numbers).
#define SKIN_B_PARAMS 43
#define SKIN_B_OCCUPIED 44
#define SKIN_B_CELLS 45
#define SKIN_B_ENTRIES 46
#define SKIN_B_TRIS 47
#define SKIN_B_STATE 48
#define SKIN_B_WET 49

// state words
#define SKIN_S_TRIS 0
#define SKIN_S_ENTRIES 1
#define SKIN_S_TRI_OVERFLOW 2
#define SKIN_S_ENTRY_OVERFLOW 3
#define SKIN_S_CELL_CLAMP 4
#define SKIN_S_LOOKUPS 5
#define SKIN_S_CONTACTS 6
#define SKIN_S_PUSHES 7
#define SKIN_S_CROSSINGS 8
#define SKIN_S_WET_FULL 9
#define SKIN_S_LIST_CLAMP 10
#define SKIN_S_ACTIVE_PEOPLE 11
#define SKIN_S_PERSON_ACTIVE 16
#define SKIN_S_AWAKE_BOX 48
// the scan's block totals and starts, after the cells' counts, starts and cursors
#define CELL_BLOCK FLUID_GROUP_SIZE

#define PASS_MARK 0u
#define PASS_INSERT 1u
#define PASS_COLLIDE 2u
#define PASS_BLOCKS 3u
#define PASS_SCAN 4u
#define PASS_SCATTER 5u

// cells one triangle may cover, coarse test and fine insert; past these it counts a clamp
#define MAX_COARSE_CELLS 64
#define MAX_FINE_CELLS 512
// coarse cells one particle marks along its frame's path, fine cells it walks along its substep's
#define MAX_MARK_CELLS 8
#define MAX_PATH_CELLS 6
// entries one particle's lanes walk in one cell, past this it counts a clamp
#define MAX_WALK 512u
// wet table: open addressing, this many probes
#define WET_PROBES 8u
#define WET_WORDS 10u
#define LANE_PARTICLES (FLUID_GROUP_SIZE / FLUID_LANES)

struct SkinParams
{
    float fineCell;
    float invFineCell;
    float coarseCell;
    float invCoarseCell;
    float reach;
    float frameTravel;
    float listReach;
    float skinTravel;
    float spare2;
    float spare3;
    float thickness;
    float wetCell;
    uint triCap;
    uint entryCap;
    uint cellMask;
    uint occupiedMask;
    uint wetMask;
    uint wetOn;
    uint people;
    uint spare0;
    vec4 boxes[64];
};

struct SkinTriangle
{
    vec3 centre;
    float radius;
    vec3 a;
    uint flags;
    vec3 b;
    uint triangle;
    vec3 c;
    uint personPart;
    vec3 lastA;
    float spare0;
    vec3 lastB;
    float spare1;
    vec3 lastC;
    float spare2;
};

layout(set = 0, binding = SKIN_B_PARAMS, std430) readonly buffer SkinParamsBuffer { SkinParams p; } prm;
layout(set = 0, binding = SKIN_B_OCCUPIED, std430) buffer SkinOccupied { uint v[]; } occupied;
// counts, then starts within their block, then fill cursors (cellMask + 1 words each), then per block of CELL_BLOCK
// cells its total and its start
layout(set = 0, binding = SKIN_B_CELLS, std430) buffer SkinCells { uint v[]; } cells;
layout(set = 0, binding = SKIN_B_ENTRIES, std430) buffer SkinEntries { uint v[]; } entries;
layout(set = 0, binding = SKIN_B_TRIS, std430) buffer SkinTris { SkinTriangle t[]; } tris;
layout(set = 0, binding = SKIN_B_STATE, std430) buffer SkinState { uint v[]; } state;
layout(set = 0, binding = SKIN_B_WET, std430) buffer SkinWet { uint v[]; } wet;

shared uint sFlag;
shared uint sPartial[FLUID_GROUP_SIZE];
shared float sKey[FLUID_GROUP_SIZE];
shared uint sTri[FLUID_GROUP_SIZE];
shared vec4 sA[FLUID_GROUP_SIZE];
shared vec4 sB[FLUID_GROUP_SIZE];
shared vec4 sC[FLUID_GROUP_SIZE];

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

uint cellCount() { return prm.p.cellMask + 1u; }

// A cell's start in the entry list: its block's start plus its own within the block.
uint cellStart(uint h)
{
    uint n = cellCount();
    return cells.v[3u * n + n / uint(CELL_BLOCK) + h / uint(CELL_BLOCK)] + cells.v[n + h];
}

uint fineHash(ivec3 c)
{
    return fluidHashCell(c, 0u, prm.p.cellMask);
}

uint coarseHash(ivec3 c)
{
    return fluidHashCell(c, 7u, prm.p.occupiedMask);
}

bool coarseMarked(ivec3 c)
{
    uint h = coarseHash(c);
    return (occupied.v[h >> 5] & (1u << (h & 31u))) != 0u;
}

// ---- pass 0: mark ----

void markParticle(uint i)
{
    FluidParticle P = pb.p[i];
    if (fluidPhaseOf(P.phase) == FLUID_PHASE_DEAD)
        return;
    uint f = fluidFrameOf(P.material);
    vec3 x = P.previous;
    vec3 v = P.velocity;
    if (f != 0u && f < hdr.h.frames)
    {
        vec4 q;
        vec3 o;
        float s;
        framePose(f, 0.0, q, o, s);
        x = fluidToWorld(P.previous, q, o, s);
        v = fluidQuatRotate(q, v) * s;
    }
    vec3 end = x + v * prm.p.frameTravel;
    float len = length(end - x);
    int steps = clamp(int(ceil(len / (0.5 * prm.p.coarseCell))), 0, MAX_MARK_CELLS - 1);
    uint last = 0xFFFFFFFFu;
    for (int s = 0; s <= steps; s++)
    {
        vec3 w = steps > 0 ? mix(x, end, float(s) / float(steps)) : x;
        uint h = coarseHash(ivec3(floor(w * prm.p.invCoarseCell)));
        if (h == last)
            continue;
        last = h;
        atomicOr(occupied.v[h >> 5], 1u << (h & 31u));
    }
    // every person whose bone box (grown by the reach) the path's box meets
    vec3 lo = min(x, end) - prm.p.reach, hi = max(x, end) + prm.p.reach;
    uint people_ = min(prm.p.people, uint(FLUID_S_PEOPLE_MAX));
    for (uint k = 0u; k < people_; k++)
    {
        if (all(lessThanEqual(lo, prm.p.boxes[2u * k + 1u].xyz)) && all(greaterThanEqual(hi, prm.p.boxes[2u * k].xyz))
            && state.v[SKIN_S_PERSON_ACTIVE + k] == 0u)
            state.v[SKIN_S_PERSON_ACTIVE + k] = 1u;
    }
}

// ---- pass 1: insert, pass 5: scatter ----

uint personOfTriangle(uint tri)
{
    uint n = min(hdr.h.people, uint(FLUID_S_PEOPLE_MAX));
    for (uint p = 0u; p < n; p++)
    {
        FluidPersonRecord R = people.p[p];
        if (tri >= R.triangleStart && tri < R.triangleStart + R.triangleCount)
            return p;
    }
    return FLUID_NONE;
}

// The fine cells a kept triangle covers: its two poses' box grown by the reach, less the cells both poses' planes
// pass farther than the reach on the same side. Counting and scattering walk the very same cells.
uint coverCells(SkinTriangle T, uint slot, bool scatter)
{
    vec3 lo = min(min(min(T.a, T.b), min(T.c, T.lastA)), min(T.lastB, T.lastC));
    vec3 hi = max(max(max(T.a, T.b), max(T.c, T.lastA)), max(T.lastB, T.lastC));
    float reach = prm.p.reach;
    float half_ = 0.8660254 * prm.p.fineCell;
    // a cell is in when its middle is within the list reach (plus its own half diagonal and how far the triangle moves
    // over the frame) of the triangle now
    float motion = max(max(length(T.a - T.lastA), length(T.b - T.lastB)), length(T.c - T.lastC));
    float within = prm.p.listReach + half_ + motion;
    reach = prm.p.listReach + motion;
    ivec3 f0 = ivec3(floor((lo - reach) * prm.p.invFineCell));
    ivec3 f1 = ivec3(floor((hi + reach) * prm.p.invFineCell));
    ivec3 fn = f1 - f0 + 1;
    if (fn.x * fn.y * fn.z > MAX_FINE_CELLS)
    {
        if (!scatter)
            atomicAdd(state.v[SKIN_S_CELL_CLAMP], 1u);
        ivec3 mid = ivec3(floor((T.a + T.b + T.c) * (prm.p.invFineCell / 3.0)));
        f0 = max(f0, mid - 3);
        f1 = min(f1, mid + 4);
    }
    uint covered = 0u;
    uint cellsN = cellCount();
    for (int z = f0.z; z <= f1.z; z++)
    {
        for (int y = f0.y; y <= f1.y; y++)
        {
            for (int x = f0.x; x <= f1.x; x++)
            {
                vec3 centre = (vec3(x, y, z) + 0.5) * prm.p.fineCell;
                if (distance(centre, fluidClosestOnTriangle(centre, T.a, T.b, T.c)) > within)
                    continue;
                uint h = fineHash(ivec3(x, y, z));
                if (scatter)
                {
                    uint at = cellStart(h) + atomicAdd(cells.v[2u * cellsN + h], 1u);
                    if (at < prm.p.entryCap)
                        entries.v[at] = slot;
                }
                else
                    atomicAdd(cells.v[h], 1u);
                covered++;
            }
        }
    }
    return covered;
}

void insertTriangle(uint tri)
{
    if (tri >= hdr.h.contactTriangles)
        return;
    uint person = personOfTriangle(tri);
    if (person == FLUID_NONE || state.v[SKIN_S_PERSON_ACTIVE + person] == 0u || (people.p[person].flags & FLUID_PERSON_FROZEN) != 0u)
        return;
    uint i0 = contactIndex.v[tri * 3u];
    uint i1 = contactIndex.v[tri * 3u + 1u];
    uint i2 = contactIndex.v[tri * 3u + 2u];
    FluidSkinnedVertex A = skinned.v[i0];
    FluidSkinnedVertex B = skinned.v[i1];
    FluidSkinnedVertex C = skinned.v[i2];
    vec3 lo = min(min(min(A.position, B.position), min(C.position, A.lastPosition)), min(B.lastPosition, C.lastPosition));
    vec3 hi = max(max(max(A.position, B.position), max(C.position, A.lastPosition)), max(B.lastPosition, C.lastPosition));
    if (any(isnan(lo)) || any(isnan(hi)) || any(isinf(lo)) || any(isinf(hi)))
        return;
    float reach = prm.p.reach;

    // Near any marked coarse cell at all?
    ivec3 c0 = ivec3(floor((lo - reach) * prm.p.invCoarseCell));
    ivec3 c1 = ivec3(floor((hi + reach) * prm.p.invCoarseCell));
    ivec3 cn = c1 - c0 + 1;
    bool near_ = false;
    if (cn.x * cn.y * cn.z > MAX_COARSE_CELLS)
        near_ = true;
    else
    {
        for (int z = c0.z; z <= c1.z && !near_; z++)
            for (int y = c0.y; y <= c1.y && !near_; y++)
                for (int x = c0.x; x <= c1.x && !near_; x++)
                    near_ = coarseMarked(ivec3(x, y, z));
    }
    if (!near_)
        return;

    uint slot = atomicAdd(state.v[SKIN_S_TRIS], 1u);
    if (slot >= prm.p.triCap)
    {
        atomicAdd(state.v[SKIN_S_TRI_OVERFLOW], 1u);
        return;
    }
    SkinTriangle T;
    T.centre = 0.5 * (lo + hi);
    T.radius = 0.5 * length(hi - lo);
    T.a = A.position;
    T.b = B.position;
    T.c = C.position;
    T.lastA = A.lastPosition;
    T.lastB = B.lastPosition;
    T.lastC = C.lastPosition;
    T.flags = (contactVerts.v[i0].bone0 & FLUID_CONTACT_TWO_SIDED) != 0u ? 1u : 0u;
    T.triangle = tri;
    T.personPart = (person << 8) | (A.region & 255u);
    T.spare0 = 0.0;
    T.spare1 = 0.0;
    T.spare2 = 0.0;
    tris.t[slot] = T;
    atomicAdd(state.v[SKIN_S_ENTRIES], coverCells(T, slot, false));
}

void scatterTriangle(uint slot)
{
    if (slot >= min(state.v[SKIN_S_TRIS], prm.p.triCap))
        return;
    coverCells(tris.t[slot], slot, true);
}

// ---- pass 4: scan, pass 3: blocks ----

// A workgroup per CELL_BLOCK cells: each cell's start within the block, and the block's total.
void scanBlock(uint block, uint lane)
{
    uint n = cellCount();
    uint h = block * uint(CELL_BLOCK) + lane;
    uint count = h < n ? cells.v[h] : 0u;
    sPartial[lane] = count;
    barrier();
    for (uint off = 1u; off < uint(CELL_BLOCK); off <<= 1)
    {
        uint add = lane >= off ? sPartial[lane - off] : 0u;
        barrier();
        sPartial[lane] += add;
        barrier();
    }
    if (h < n)
        cells.v[n + h] = sPartial[lane] - count;
    if (lane == uint(CELL_BLOCK) - 1u && block < n / uint(CELL_BLOCK))
        cells.v[3u * n + block] = sPartial[lane];
}

// One workgroup: every block's start from the block totals.
void scanBlocks(uint lane)
{
    uint n = cellCount();
    uint blocks = n / uint(CELL_BLOCK);
    uint per = (blocks + uint(FLUID_GROUP_SIZE) - 1u) / uint(FLUID_GROUP_SIZE);
    uint from = lane * per;
    uint to = min(from + per, blocks);
    uint sum = 0u;
    for (uint b = from; b < to; b++)
        sum += cells.v[3u * n + b];
    sPartial[lane] = sum;
    barrier();
    if (lane == 0u)
    {
        uint run = 0u;
        for (uint k = 0u; k < uint(FLUID_GROUP_SIZE); k++)
        {
            uint t = sPartial[k];
            sPartial[k] = run;
            run += t;
        }
        if (run > prm.p.entryCap)
            state.v[SKIN_S_ENTRY_OVERFLOW] = run - prm.p.entryCap;
    }
    barrier();
    uint run = sPartial[lane];
    for (uint b = from; b < to; b++)
    {
        cells.v[3u * n + blocks + b] = run;
        run += cells.v[3u * n + b];
    }
}

// ---- pass 2: collide ----

struct Pose3
{
    vec3 a;
    vec3 b;
    vec3 c;
    vec3 n;
    bool ok;
};

Pose3 poseAt(SkinTriangle T, float t)
{
    Pose3 r;
    r.a = mix(T.lastA, T.a, t);
    r.b = mix(T.lastB, T.b, t);
    r.c = mix(T.lastC, T.c, t);
    vec3 n = cross(r.b - r.a, r.c - r.a);
    float l = length(n);
    r.ok = l > 1e-14;
    r.n = r.ok ? n / l : vec3(0.0, 1.0, 0.0);
    return r;
}

// The plane coordinates of p over a triangle: x, y along its edges from a (the barycentric weights of b and c), z
// along its unit normal.
vec3 planeCoords(vec3 p, Pose3 T)
{
    vec3 e1 = T.b - T.a, e2 = T.c - T.a, d = p - T.a;
    float d00 = dot(e1, e1), d01 = dot(e1, e2), d11 = dot(e2, e2), d20 = dot(d, e1), d21 = dot(d, e2);
    float den = d00 * d11 - d01 * d01;
    if (abs(den) < 1e-24)
        return vec3(0.0, 0.0, dot(d, T.n));
    return vec3((d11 * d20 - d01 * d21) / den, (d00 * d21 - d01 * d20) / den, dot(d, T.n));
}

vec3 fromPlane(vec3 k, Pose3 T)
{
    return T.a + k.x * (T.b - T.a) + k.y * (T.c - T.a) + k.z * T.n;
}

// The lanes' candidates are in shared memory: every lane picks the same best one (smallest key, then smallest
// triangle index) and the next writer waits for everyone to have read.
uint pickLane(uint base)
{
    uint best = base;
    for (uint l = 1u; l < uint(FLUID_LANES); l++)
    {
        uint j = base + l;
        if (sKey[j] < sKey[best] || (sKey[j] == sKey[best] && sTri[j] < sTri[best]))
            best = j;
    }
    return best;
}

void wetSample(uint personPart, vec3 world, vec3 normal, float volume, uint substance)
{
    uint person = personPart >> 8;
    uint part = personPart & 255u;
    if (person >= hdr.h.people || part >= uint(FLUID_BODY_PARTS))
        return;
    FluidPersonRecord R = people.p[person];
    uint bi = R.boneStart + part;
    if (part >= R.boneCount || bi >= hdr.h.bones)
        return;
    FluidBoneRecord B = bones.b[bi];
    if (dot(B.row0.xyz, B.row0.xyz) < 1e-12)
        return;
    vec3 local = fluidAffineInverse(B.row0, B.row1, B.row2, world);
    vec3 tip = fluidAffineInverse(B.row0, B.row1, B.row2, world + normal * 0.01);
    vec3 ln = tip - local;
    ln = length(ln) > 1e-9 ? normalize(ln) : vec3(0.0, 1.0, 0.0);
    ivec3 cell = ivec3(floor(local / max(prm.p.wetCell, 1e-4)));
    uvec3 u = uvec3(cell + 64) & 127u;
    uint key = 0x80000000u | ((person & 31u) << 25) | ((part & 15u) << 21) | (u.x << 14) | (u.y << 7) | u.z;
    uint h = fluidHashWord(key) & prm.p.wetMask;
    for (uint k = 0u; k < WET_PROBES; k++)
    {
        uint e = ((h + k) & prm.p.wetMask) * WET_WORDS;
        uint was = atomicCompSwap(wet.v[e], 0u, key);
        if (was == 0u || was == key)
        {
            atomicAdd(wet.v[e + 1u], 1u);
            atomicAdd(wet.v[e + 2u], uint(clamp(volume * 1e12, 0.0, 4.0e6)));
            atomicAdd(wet.v[e + 3u], fluidFixed(local.x, 1e-4));
            atomicAdd(wet.v[e + 4u], fluidFixed(local.y, 1e-4));
            atomicAdd(wet.v[e + 5u], fluidFixed(local.z, 1e-4));
            atomicAdd(wet.v[e + 6u], fluidFixed(ln.x, 1e-3));
            atomicAdd(wet.v[e + 7u], fluidFixed(ln.y, 1e-3));
            atomicAdd(wet.v[e + 8u], fluidFixed(ln.z, 1e-3));
            atomicMax(wet.v[e + 9u], substance + 1u);
            return;
        }
    }
    atomicAdd(state.v[SKIN_S_WET_FULL], 1u);
}

void collide(uint gid, uint lid)
{
    uint i = gid / uint(FLUID_LANES);
    uint lane = gid % uint(FLUID_LANES);
    uint base = lid - lane;
    uint awake = counters.v[FLUID_C_AWAKE];
    bool inRange = i < awake;
    uint si = inRange ? i : 0u;
    if (lid == 0u)
        sFlag = 0u;
    barrier();

    FluidParticle P = pb.p[si];
    bool alive = inRange && fluidPhaseOf(P.phase) != FLUID_PHASE_DEAD;
    uint f = fluidFrameOf(P.material);
    bool inFrame = f != 0u && f < hdr.h.frames;
    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    float tPrev = float(pc.c.substep) / n;
    vec4 fq, fqp;
    vec3 fpos, fposp;
    float fscale, fscalep;
    framePose(f, t, fq, fpos, fscale);
    framePose(f, tPrev, fqp, fposp, fscalep);
    vec3 p = inFrame ? fluidToWorld(P.position, fq, fpos, fscale) : P.position;
    vec3 x = inFrame ? fluidToWorld(P.previous, fqp, fposp, fscalep) : P.previous;

    // The cells along the substep's path, start to end; the last is where it ends.
    uint pathStart[MAX_PATH_CELLS];
    uint pathCount_[MAX_PATH_CELLS];
    uint paths = 0u;
    uint cellsN = cellCount();
    float travel = length(p - x);
    if (alive)
    {
        int steps = clamp(int(ceil(travel / (0.5 * prm.p.fineCell))), 0, MAX_PATH_CELLS - 1);
        uint lastHash = 0xFFFFFFFFu;
        for (int s = 0; s <= steps; s++)
        {
            vec3 w = steps > 0 ? mix(x, p, float(s) / float(steps)) : p;
            uint h = fineHash(ivec3(floor(w * prm.p.invFineCell)));
            if (h == lastHash)
                continue;
            lastHash = h;
            uint count = min(cells.v[h], MAX_WALK);
            if (count == 0u)
                continue;
            pathStart[paths] = cellStart(h);
            pathCount_[paths] = count;
            paths++;
        }
    }
    if (paths > 0u && lane == 0u)
        atomicOr(sFlag, 1u);
    barrier();
    if (sFlag == 0u)
        return;
    bool work = paths > 0u;
    if (work && lane == 0u)
        atomicAdd(state.v[SKIN_S_LOOKUPS], 1u);

    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float dt = hdr.h.dtSubstep;
    bool lastSubstep = pc.c.substep + 1u >= hdr.h.substeps;
    float r = 0.5 * sub.spacing;
    float band = sub.stickDistance + FLUID_CONTACT_SLOP * sub.spacing;
    float thick = max(prm.p.thickness, 2.0 * r);
    float reach = r + band + thick;
    vec3 start = p;

    // 1. Crossings: the first triangle the substep's path went through, seen from the triangle's own frame. A particle
    // that moved less than its radius against the skin this substep (its own travel plus the fastest skin's) cannot
    // have gone through a surface: if it ends inside one it is within the radius of it, and the nearest push takes it
    // back out the side it came from.
    bool mayCross = travel + prm.p.skinTravel >= r;
    float myTh = 2.0;
    uint myTri = FLUID_NONE;
    vec4 myA = vec4(0.0), myB = vec4(0.0);
    for (uint c = 0u; work && mayCross && c < paths; c++)
    {
        for (uint e = lane; e < pathCount_[c]; e += uint(FLUID_LANES))
        {
            uint slot = entries.v[pathStart[c] + e];
            vec4 sphere = vec4(tris.t[slot].centre, tris.t[slot].radius);
            if (distance(p, sphere.xyz) > sphere.w + r + travel)
                continue;
            SkinTriangle T = tris.t[slot];
            Pose3 now_ = poseAt(T, t);
            Pose3 was_ = poseAt(T, tPrev);
            if (!now_.ok || !was_.ok)
                continue;
            vec3 k = planeCoords(x, was_);
            bool twoSided = (T.flags & 1u) != 0u;
            float side = twoSided ? (k.z >= 0.0 ? 1.0 : -1.0) : 1.0;
            if (!twoSided && k.z < 0.0)
                continue;
            vec3 xs = fromPlane(k, now_);
            if (dot(p - now_.a, now_.n) * side >= 0.0)
                continue;
            float th = fluidSegmentTriangle(xs, p, now_.a, now_.b, now_.c);
            if (th > 1.0)
                continue;
            if (th < myTh || (th == myTh && T.triangle < myTri))
            {
                myTh = th;
                myTri = T.triangle;
                myA = vec4(mix(xs, p, th) + now_.n * side * r, k.z * side);
                myB = vec4(now_.n * side, 0.0);
            }
        }
    }
    sKey[lid] = myTh;
    sTri[lid] = myTri;
    sA[lid] = myA;
    sB[lid] = myB;
    barrier();
    uint pick = pickLane(base);
    uint crossTri = sTri[pick];
    vec4 crossA = sA[pick];
    vec4 crossB = sB[pick];
    barrier();

    // What the start already lacked of the radius is no motion: it starts that much out.
    vec3 settle = vec3(0.0);
    uint endStart = 0u, endCount = 0u;
    if (work)
    {
        if (crossTri != FLUID_NONE)
        {
            p = crossA.xyz;
            settle = crossB.xyz * clamp(r - crossA.w, 0.0, r);
            x += settle;
            if (lane == 0u)
                atomicAdd(state.v[SKIN_S_CROSSINGS], 1u);
        }
        uint h = fineHash(ivec3(floor(p * prm.p.invFineCell)));
        endCount = min(cells.v[h], MAX_WALK);
        endStart = cellStart(h);
    }

    // 2. The nearest triangle: push out to the radius along its normal, and remember it as a contact.
    float myDist = 1e30;
    myTri = FLUID_NONE;
    myA = vec4(0.0);
    myB = vec4(0.0);
    vec4 myC = vec4(0.0);
    for (uint e = lane; e < endCount; e += uint(FLUID_LANES))
    {
        uint slot = entries.v[endStart + e];
        vec4 sphere = vec4(tris.t[slot].centre, tris.t[slot].radius);
        if (distance(p, sphere.xyz) > sphere.w + reach)
            continue;
        SkinTriangle T = tris.t[slot];
        Pose3 now_ = poseAt(T, t);
        if (!now_.ok)
            continue;
        bool twoSided = (T.flags & 1u) != 0u;
        vec3 q = fluidClosestOnTriangle(p, now_.a, now_.b, now_.c);
        vec3 dv = p - q;
        float dist = length(dv);
        if (dist > reach)
            continue;
        float sPlane = dot(p - now_.a, now_.n);
        float side = 1.0;
        Pose3 was_;
        float kz = sPlane;
        bool haveStart = false;
        if (twoSided)
        {
            was_ = poseAt(T, tPrev);
            kz = was_.ok ? planeCoords(x, was_).z : sPlane;
            haveStart = true;
            side = kz >= 0.0 ? 1.0 : -1.0;
        }
        else if (sPlane < -thick)
            continue;
        if (dist < myDist || (dist == myDist && T.triangle < myTri))
        {
            if (!haveStart)
            {
                was_ = poseAt(T, tPrev);
                kz = was_.ok ? planeCoords(x, was_).z : sPlane;
            }
            myDist = dist;
            myTri = T.triangle;
            float along = dot(dv, now_.n) * side;
            bool front = along > 1e-7 && dist > 1e-7;
            vec3 k = planeCoords(q, now_);
            myA = vec4(q, front ? dist : -dist);
            myB = vec4(front ? dv / dist : now_.n * side, kz * side);
            myC = vec4(was_.ok ? fromPlane(vec3(k.xy, 0.0), was_) : q, uintBitsToFloat(T.personPart));
        }
    }
    sKey[lid] = myDist;
    sTri[lid] = myTri;
    sA[lid] = myA;
    sB[lid] = myB;
    sC[lid] = myC;
    barrier();
    pick = pickLane(base);
    uint bestTri = sTri[pick];
    vec3 bestQ = sA[pick].xyz;
    float bestSep = sA[pick].w;
    vec3 bestDir = sB[pick].xyz;
    float bestStart = sB[pick].w;
    vec3 bestPrevQ = sC[pick].xyz;
    uint bestPart = floatBitsToUint(sC[pick].w);
    barrier();

    bool pushed = work && bestTri != FLUID_NONE && bestSep < r;
    if (pushed)
    {
        p = bestQ + bestDir * r;
        // what only undoes a depth it already had at the start is no motion either
        settle += bestDir * clamp(r - bestStart, 0.0, r - bestSep);
        if (lane == 0u)
            atomicAdd(state.v[SKIN_S_PUSHES], 1u);
    }

    // 3. Creases: the push can leave it inside a second surface (an armpit, between fingers, a lip seam). Two more
    // rounds push it out of whichever triangle it is deepest in. Every lane runs both rounds: they meet at barriers.
    for (uint round_ = 0u; round_ < 2u; round_++)
    {
        float mySep = r - 1e-5;
        myTri = FLUID_NONE;
        myA = vec4(0.0);
        myB = vec4(0.0);
        uint rStart = 0u, rCount = 0u;
        if (pushed)
        {
            uint h = fineHash(ivec3(floor(p * prm.p.invFineCell)));
            rCount = min(cells.v[h], MAX_WALK);
            rStart = cellStart(h);
        }
        for (uint e = lane; e < rCount; e += uint(FLUID_LANES))
        {
            uint slot = entries.v[rStart + e];
            vec4 sphere = vec4(tris.t[slot].centre, tris.t[slot].radius);
            if (distance(p, sphere.xyz) > sphere.w + r)
                continue;
            SkinTriangle T = tris.t[slot];
            Pose3 now_ = poseAt(T, t);
            if (!now_.ok)
                continue;
            vec3 q = fluidClosestOnTriangle(p, now_.a, now_.b, now_.c);
            vec3 dv = p - q;
            float dist = length(dv);
            if (dist >= r - 1e-5)
                continue;
            bool twoSided = (T.flags & 1u) != 0u;
            float side = 1.0;
            if (twoSided)
            {
                Pose3 was_ = poseAt(T, tPrev);
                side = (was_.ok ? planeCoords(x, was_).z : dot(p - now_.a, now_.n)) >= 0.0 ? 1.0 : -1.0;
            }
            else if (dot(p - now_.a, now_.n) < -thick)
                continue;
            float along = dot(dv, now_.n) * side;
            bool front = along > 1e-7 && dist > 1e-7;
            float sep = front ? dist : -dist;
            if (sep < mySep || (sep == mySep && T.triangle < myTri))
            {
                mySep = sep;
                myTri = T.triangle;
                myA = vec4(q, 0.0);
                myB = vec4(front ? dv / dist : now_.n * side, 0.0);
            }
        }
        sKey[lid] = myTri != FLUID_NONE ? mySep : 1e30;
        sTri[lid] = myTri;
        sA[lid] = myA;
        sB[lid] = myB;
        barrier();
        pick = pickLane(base);
        uint deepTri = sTri[pick];
        vec3 deepQ = sA[pick].xyz;
        vec3 deepDir = sB[pick].xyz;
        barrier();
        if (pushed && deepTri != FLUID_NONE)
        {
            p = deepQ + deepDir * r;
            if (lane == 0u)
                atomicAdd(state.v[SKIN_S_PUSHES], 1u);
        }
        else
            pushed = false;
    }

    if (!work || lane != 0u)
        return;
    if (bestTri == FLUID_NONE)
    {
        if (crossTri != FLUID_NONE)
        {
            pb.p[i].position = inFrame ? fluidToLocal(p, fq, fpos, fscale) : p;
            pb.p[i].previous = P.previous + (inFrame ? fluidQuatInverseRotate(fq, settle) / fscale : settle);
        }
        return;
    }
    if (dot(settle, settle) > 0.0)
        pb.p[i].previous = P.previous + (inFrame ? fluidQuatInverseRotate(fq, settle) / fscale : settle);
    float contactDist = max(bestSep, r);
    // how far this pass moved it out along the contact normal: friction's normal displacement, as predict's depth
    float depth = max(dot(p - start, bestDir), 0.0);

    uint person = bestPart >> 8;
    uint part = bestPart & 255u;
    FluidPersonRecord R = people.p[min(person, max(hdr.h.people, 1u) - 1u)];
    uint b = i * FLUID_SLOT_STRIDE;
    float predictBest = slots.v[b + 3u].z;
    bool touching = contactDist < r + band;
    if (touching && contactDist <= predictBest)
    {
        // Who may stick here: the person's own mode, the frozen bit, and the wearer's hidden pours.
        float stick = (R.flags & FLUID_PERSON_STICK_MASK) == 0u && (R.flags & FLUID_PERSON_FROZEN) == 0u ? R.stickiness : 0.0;
        if ((R.flags & FLUID_PERSON_LOCAL) != 0u)
        {
            uint origin = fluidOriginOf(P.material);
            for (uint k = 0u; k < hdr.h.hiddenOrigins; k++)
                stick = hidden.v[k] == origin ? 0.0 : stick;
        }
        float friction = R.skinFriction > 0.0 ? R.skinFriction : 1.0;
        float wetting = R.skinWetting > 0.0 ? R.skinWetting : 1.0;

        // Wetting (Akinci adhesion) toward the skin, as predict does toward its own nearest surface.
        float h = sub.smoothingRadius;
        if (contactDist < h && sub.adhesion > 0.0)
        {
            float span = max(h - r, 1e-6);
            float shape = clamp(1.0 - (contactDist - r) / span, 0.0, 1.0);
            float accel = sub.adhesion * wetting * sub.surfaceTension * sub.spacing / max(P.mass, 1e-12) * shape;
            float pull = min(accel * dt * dt, max(contactDist - r, 0.0) * 0.5);
            p -= bestDir * pull;
        }

        vec3 vSurf = (bestQ - bestPrevQ) / max(dt, 1e-9);
        vec3 nl = inFrame ? normalize(fluidQuatInverseRotate(fq, bestDir)) : bestDir;
        vec3 sl = inFrame ? fluidToLocal(bestQ, fq, fpos, fscale) : bestQ;
        vec3 vl = inFrame ? fluidQuatInverseRotate(fq, vSurf) / fscale : vSurf;
        slots.v[b + 1u] = vec4(nl, dot(sl, nl));
        slots.v[b + 2u] = vec4(vl, uintBitsToFloat(fluidTargetKey(FLUID_ANCHOR_SKIN, bestTri)));
        slots.v[b + 3u] = vec4(friction, stick, contactDist, 0.0);
        slots.v[b].w = max(slots.v[b].w, depth);
        atomicAdd(state.v[SKIN_S_CONTACTS], 1u);

        if (lastSubstep && person < uint(FLUID_S_PEOPLE_MAX) && part < uint(FLUID_BODY_PARTS))
        {
            uint s = FLUID_S_PARTS + (person * uint(FLUID_BODY_PARTS) + part) * 4u;
            uint substance = fluidSubstanceOf(P.material);
            atomicAdd(stats.v[s], 1u);
            atomicMax(stats.v[s + 1u], floatBitsToUint(depth));
            atomicMax(stats.v[s + 2u], floatBitsToUint(length(vSurf)));
            atomicMax(stats.v[s + 3u], (floatBitsToUint(depth) & 0xFFFFFF00u) | (substance & 255u));
            if (prm.p.wetOn != 0u)
                wetSample(bestPart, bestQ, bestDir, P.volume, substance);
        }
    }
    pb.p[i].position = inFrame ? fluidToLocal(p, fq, fpos, fscale) : p;
}

void main()
{
    uint gid = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    if (pc.c.pass == PASS_COLLIDE)
    {
        collide(gid, lid);
        return;
    }
    if (pc.c.pass == PASS_SCAN)
    {
        scanBlock(gl_WorkGroupID.x, lid);
        return;
    }
    if (pc.c.pass == PASS_BLOCKS)
    {
        scanBlocks(lid);
        return;
    }
    if (pc.c.pass == PASS_INSERT)
    {
        insertTriangle(gid);
        return;
    }
    if (pc.c.pass == PASS_SCATTER)
    {
        scatterTriangle(gid);
        return;
    }
    if (gid >= counters.v[FLUID_C_AWAKE])
        return;
    markParticle(gid);
}
