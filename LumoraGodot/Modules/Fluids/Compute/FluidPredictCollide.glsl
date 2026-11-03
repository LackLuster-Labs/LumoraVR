// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Pass 0 gathers neighbour forces with FLUID_LANES threads per particle and writes the prediction to B.
// Pass 1 uses one thread per particle for collider sweeps, proxy and soft-body contacts, adhesion and bounds.
// It replaces B's prediction with the collision result and records the contact for friction and anchoring.
// Moving colliders are swept between their previous and current substep poses. -xlinka

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
FLUID_DECLARE_ANCHORS
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_AUX
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_SYSTEMS
FLUID_DECLARE_FRAMES
FLUID_DECLARE_COLLIDERS
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_STATS
FLUID_DECLARE_TRIANGLES
FLUID_DECLARE_TRI_GRIDS
FLUID_DECLARE_TRI_CELLS
FLUID_DECLARE_TRI_INDEX
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_BONES
FLUID_DECLARE_PROXIES
FLUID_DECLARE_SOFT
FLUID_DECLARE_PUSH

shared vec4 sA[FLUID_GROUP_SIZE];
shared vec4 sB[FLUID_GROUP_SIZE];
shared vec4 sC[FLUID_GROUP_SIZE];
// Shared records hold substep poses, replacing the frame poses from the upload. -xlinka
#define COLLIDER_TILE_SIZE 32
shared FluidColliderRecord sColliders[COLLIDER_TILE_SIZE];

// how hard water spins up its own small eddies again; viscous kinds get none. Kept low: confinement adds energy,
// and at 0.3 a sloshing tank never calmed.
#define VORTICITY_STRENGTH 0.05
#define MAX_TRIANGLES_PER_CELL 96u
#define SWEEP_STEPS 16

// The nearest surface this substep, world space until the end.
struct Contact
{
    float distance;
    vec3 normal;
    vec3 point;
    vec3 velocity;
    uint key;
    float friction;
    float stickiness;
    float wetting;
    float depth;
    float bounce;
};

void reduceLanes(uint lid, uint lane)
{
    barrier();
    for (uint off = FLUID_LANES / 2u; off > 0u; off >>= 1)
    {
        if (lane < off)
        {
            sA[lid] += sA[lid + off];
            sB[lid] += sB[lid + off];
            sC[lid] += sC[lid + off];
        }
        barrier();
    }
}

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

float primitiveSdf(uint kind, vec3 lp, vec3 halfExtents, float radius)
{
    if (kind == FLUID_COLLIDER_SPHERE)
        return length(lp) - radius;
    if (kind == FLUID_COLLIDER_CAPSULE)
        return fluidSdfCapsule(lp, halfExtents.y, radius);
    if (kind == FLUID_COLLIDER_CYLINDER)
        return fluidSdfCylinder(lp, halfExtents.y, radius);
    return fluidSdfBox(lp, halfExtents);
}

vec3 primitiveNormal(uint kind, vec3 lp, vec3 halfExtents, float radius, float e)
{
    vec2 k = vec2(1.0, -1.0);
    vec3 g = k.xyy * primitiveSdf(kind, lp + k.xyy * e, halfExtents, radius)
           + k.yyx * primitiveSdf(kind, lp + k.yyx * e, halfExtents, radius)
           + k.yxy * primitiveSdf(kind, lp + k.yxy * e, halfExtents, radius)
           + k.xxx * primitiveSdf(kind, lp + k.xxx * e, halfExtents, radius);
    float len = length(g);
    return len > 1e-12 ? g / len : vec3(0.0, 1.0, 0.0);
}

void consider(inout Contact best, float distance, vec3 normal, vec3 point, vec3 velocity, uint key, float friction, float stickiness, float wetting, float depth, float bounce)
{
    if (distance < best.distance)
    {
        best.bounce = bounce;
        best.wetting = wetting;
        best.depth = depth;
        best.distance = distance;
        best.normal = normal;
        best.point = point;
        best.velocity = velocity;
        best.key = key;
        best.friction = friction;
        best.stickiness = stickiness;
    }
}

void addImpulse(uint body, vec3 j, vec3 arm)
{
    if (body >= uint(FLUID_MAX_IMPULSE_BODIES))
        return;
    uint b = FLUID_S_IMPULSES + body * 8u;
    vec3 l = cross(arm, j);
    atomicAdd(stats.v[b + 0u], fluidFixed(j.x, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 1u], fluidFixed(j.y, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 2u], fluidFixed(j.z, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 3u], fluidFixed(l.x, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 4u], fluidFixed(l.y, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 5u], fluidFixed(l.z, FLUID_IMPULSE_UNIT));
    atomicAdd(stats.v[b + 6u], 1u);
}

vec3 triVertex(float v[3])
{
    return vec3(v[0], v[1], v[2]);
}

// Triangles of a mesh collider's cached grid, in the collider's local space (scale divided out, so rl is the
// particle radius in local units). The side the particle started the substep on decides which way it is pushed,
// so a wall thinner than a particle still holds.
void collideMesh(FluidColliderRecord C, uint grid, vec3 lx, inout vec3 lp, float rl, inout float bestLocal, inout vec3 bestNormal, inout vec3 bestPoint)
{
    FluidTriangleGridHeader G = triGrids.g[grid];
    for (uint pass_ = 0u; pass_ < 2u; pass_++)
    {
        vec3 probe = pass_ == 0u ? lp : lx;
        ivec3 ci = ivec3(floor((probe - G.origin) / G.cellSize));
        if (any(lessThan(ci, ivec3(0))) || ci.x >= int(G.dimX) || ci.y >= int(G.dimY) || ci.z >= int(G.dimZ))
            continue;
        if (pass_ == 1u && ivec3(floor((lp - G.origin) / G.cellSize)) == ci)
            continue;
        uint cell = G.cellStart + uint(ci.x) + G.dimX * (uint(ci.y) + G.dimY * uint(ci.z));
        uint start = triCells.c[cell].start;
        uint count = min(triCells.c[cell].count, MAX_TRIANGLES_PER_CELL);
        for (uint k = 0u; k < count; k++)
        {
            FluidTriangle T = triangles.t[G.triangleStart + triIndex.v[G.indexStart + start + k]];
            vec3 a = triVertex(T.a);
            vec3 b = triVertex(T.b);
            vec3 c = triVertex(T.c);
            vec3 n = cross(b - a, c - a);
            float nl = length(n);
            if (nl < 1e-12)
                continue;
            n /= nl;
            float side = dot(lx - a, n) >= 0.0 ? 1.0 : -1.0;
            float th = fluidSegmentTriangle(lx, lp, a, b, c);
            if (th <= 1.0)
                lp = mix(lx, lp, th) + n * side * rl;
            vec3 q = fluidClosestOnTriangle(lp, a, b, c);
            vec3 dv = lp - q;
            float dist = length(dv);
            vec3 dir = (dist > 1e-7 && dot(dv, n) * side > 0.0) ? dv / dist : n * side;
            if (dist < rl || dot(dv, n) * side < 0.0)
            {
                lp = q + dir * rl;
                dist = rl;
            }
            if (dist < bestLocal)
            {
                bestLocal = dist;
                bestNormal = dir;
                bestPoint = q;
            }
        }
    }
}

void collideColliders(vec3 x, inout vec3 p, float r, float band, float mass, float dt, float t, float tPrev, bool lastSubstep, bool inRange, inout Contact best)
{
    uint n = hdr.h.colliders;
    for (uint first = 0u; first < n; first += uint(COLLIDER_TILE_SIZE))
    {
        uint lane = gl_LocalInvocationIndex;
        if (lane < uint(COLLIDER_TILE_SIZE) && first + lane < n)
        {
            FluidColliderRecord C = colliders.c[first + lane];
            vec4 qt = fluidQuatNlerp(C.lastRotation, C.rotation, t);
            vec4 qp = fluidQuatNlerp(C.lastRotation, C.rotation, tPrev);
            vec3 pt = mix(C.lastPosition, C.position, t);
            vec3 pp = mix(C.lastPosition, C.position, tPrev);
            float st = max(mix(C.lastScale, C.scale, t), 1e-6);
            float sp = max(mix(C.lastScale, C.scale, tPrev), 1e-6);
            C.rotation = qt;
            C.lastRotation = qp;
            C.position = pt;
            C.lastPosition = pp;
            C.scale = st;
            C.lastScale = sp;
            sColliders[lane] = C;
        }
        barrier();
        for (uint offset = 0u; inRange && offset < min(uint(COLLIDER_TILE_SIZE), n - first); offset++)
        {
            uint c = first + offset;
            FluidColliderRecord C = sColliders[offset];
            if ((C.flags & FLUID_COLLIDER_DRAIN) != 0u)
                continue;
            vec4 qt = C.rotation;
            vec3 pt = C.position;
            float st = C.scale;
            vec4 qp = C.lastRotation;
            vec3 pp = C.lastPosition;
            float sp = C.lastScale;
            vec3 lp = fluidToLocal(p, qt, pt, st);
            vec3 lx = fluidToLocal(x, qp, pp, sp);
            float rl = r / st;
            float bandL = (r + band) / st;
            vec3 before = p;
            float distLocal = 1e30;
            vec3 normalLocal = vec3(0.0, 1.0, 0.0);
            vec3 pointLocal = lp;
            float depthLocal = 0.0;
            if ((C.kind == FLUID_COLLIDER_MESH || C.kind == FLUID_COLLIDER_CONVEX) && C.gridIndex != FLUID_NONE)
            {
                vec3 unpushed = lp;
                collideMesh(C, C.gridIndex, lx, lp, rl, distLocal, normalLocal, pointLocal);
                depthLocal = length(lp - unpushed);
                if (distLocal > bandL)
                    continue;
            }
            else
            {
                uint kind = C.kind == FLUID_COLLIDER_CONVEX ? FLUID_COLLIDER_BOX : C.kind;
                vec3 he = C.halfExtents;
                float rad = C.radius;
                vec3 extent = abs(he);
                if (kind == FLUID_COLLIDER_SPHERE) extent = vec3(abs(rad));
                else if (kind == FLUID_COLLIDER_CAPSULE) extent = vec3(abs(rad), abs(he.y) + abs(rad), abs(rad));
                else if (kind == FLUID_COLLIDER_CYLINDER) extent = vec3(abs(rad), abs(he.y), abs(rad));
                extent += vec3(max(rl, bandL)) + 1e-5 * max(extent, vec3(1.0));
                // Reject only sweeps wholly outside the contact band; grazing and moving contacts keep the same solve. -xlinka
                if (any(greaterThan(min(lx, lp), extent)) || any(lessThan(max(lx, lp), -extent)))
                    continue;
                // Swept: when the substep started outside, march the segment and stop at the first touch.
                float sx = primitiveSdf(kind, lx, he, rad);
                if (sx >= rl * 0.5)
                {
                    float len = length(lp - lx);
                    int steps = clamp(int(ceil(len / max(0.5 * rl, 1e-6))), 1, SWEEP_STEPS);
                    float prevT = 0.0;
                    for (int s = 1; s <= steps; s++)
                    {
                        float ft = float(s) / float(steps);
                        // Resting contact must slide; sweep only a crossing into the inner contact band. -xlinka
                        if (primitiveSdf(kind, mix(lx, lp, ft), he, rad) < 0.5 * rl)
                        {
                            float lo = prevT, hi = ft;
                            for (int b = 0; b < 5; b++)
                            {
                                float mid = 0.5 * (lo + hi);
                                if (primitiveSdf(kind, mix(lx, lp, mid), he, rad) < rl)
                                    hi = mid;
                                else
                                    lo = mid;
                            }
                            lp = mix(lx, lp, lo);
                            break;
                        }
                        prevT = ft;
                    }
                }
                float sd = primitiveSdf(kind, lp, he, rad);
                if (sd > bandL)
                    continue;
                vec3 nrm = primitiveNormal(kind, lp, he, rad, 1e-4 * max(rl, 1e-4));
                depthLocal = max(rl - sd, 0.0);
                if (sd < rl)
                {
                    lp += nrm * (rl - sd);
                    sd = rl;
                }
                distLocal = sd;
                normalLocal = nrm;
                pointLocal = lp - nrm * sd;
            }
            p = fluidToWorld(lp, qt, pt, st);
            vec3 nWorld = normalize(fluidQuatRotate(qt, normalLocal));
            vec3 sWorld = fluidToWorld(pointLocal, qt, pt, st);
            vec3 sPrev = fluidToWorld(pointLocal, qp, pp, sp);
            vec3 vSurf = (sWorld - sPrev) / max(dt, 1e-9);
            uint kindKey = C.body != FLUID_NONE || (C.flags & (FLUID_COLLIDER_DYNAMIC | FLUID_COLLIDER_MOVED)) != 0u ? FLUID_ANCHOR_RIGID : FLUID_ANCHOR_STATIC;
            uint key = (C.flags & FLUID_COLLIDER_NO_STICK) != 0u ? 0u : fluidTargetKey(kindKey, c);
            consider(best, distLocal * st, nWorld, sWorld, vSurf, key, C.friction, C.stickiness, C.wetting, depthLocal * st, clamp(C.bounce, 0.0, 1.0));
            vec3 moved = p - before;
            if (C.body != FLUID_NONE && dot(moved, moved) > 0.0)
                // One centre for compound shapes keeps fixed-point torque small even far from the world origin. -xlinka
                addImpulse(C.body, -mass * moved / max(dt, 1e-9), sWorld - C.bodyCenter);
        }
        barrier();
    }
}

void proxyShape(FluidProxyRecord P, vec4 r0, vec4 r1, vec4 r2, vec3 p, out float sd, out vec3 nrm)
{
    vec3 c0 = vec3(r0.x, r1.x, r2.x);
    vec3 c1 = vec3(r0.y, r1.y, r2.y);
    vec3 c2 = vec3(r0.z, r1.z, r2.z);
    float s = length(c0);
    if (P.kind == FLUID_PROXY_BOX)
    {
        vec3 centre = fluidAffine(r0, r1, r2, P.a.xyz);
        vec3 l0 = length(c0) > 1e-9 ? c0 / length(c0) : vec3(1.0, 0.0, 0.0);
        vec3 l1 = length(c1) > 1e-9 ? c1 / length(c1) : vec3(0.0, 1.0, 0.0);
        vec3 l2 = length(c2) > 1e-9 ? c2 / length(c2) : vec3(0.0, 0.0, 1.0);
        vec3 half_ = P.b.xyz * vec3(length(c0), length(c1), length(c2));
        vec3 d = p - centre;
        vec3 lpos = vec3(dot(d, l0), dot(d, l1), dot(d, l2));
        sd = fluidSdfBox(lpos, half_);
        vec2 k = vec2(1.0, -1.0);
        float e = 1e-5;
        vec3 g = k.xyy * fluidSdfBox(lpos + k.xyy * e, half_) + k.yyx * fluidSdfBox(lpos + k.yyx * e, half_)
               + k.yxy * fluidSdfBox(lpos + k.yxy * e, half_) + k.xxx * fluidSdfBox(lpos + k.xxx * e, half_);
        g = length(g) > 1e-12 ? normalize(g) : vec3(0.0, 1.0, 0.0);
        nrm = normalize(l0 * g.x + l1 * g.y + l2 * g.z);
        return;
    }
    if (P.kind == FLUID_PROXY_SPHERE)
    {
        vec3 centre = fluidAffine(r0, r1, r2, P.a.xyz);
        vec3 d = p - centre;
        float len = length(d);
        sd = len - P.a.w * s;
        nrm = len > 1e-9 ? d / len : vec3(0.0, 1.0, 0.0);
        return;
    }
    vec3 a = fluidAffine(r0, r1, r2, P.a.xyz);
    vec3 b = fluidAffine(r0, r1, r2, P.b.xyz);
    vec3 ab = b - a;
    float tt = clamp(dot(p - a, ab) / max(dot(ab, ab), 1e-12), 0.0, 1.0);
    vec3 d = p - (a + tt * ab);
    float len = length(d);
    sd = len - P.a.w * s;
    nrm = len > 1e-9 ? d / len : vec3(0.0, 1.0, 0.0);
}

void collideProxies(vec3 x, inout vec3 p, float r, float band, float dt, float t, float tPrev, uint skipPerson, bool lastSubstep, uint substance, inout Contact best)
{
    uint people_ = min(hdr.h.people, uint(FLUID_S_PEOPLE_MAX));
    for (uint pr = 0u; pr < people_; pr++)
    {
        if (pr == skipPerson)
            continue;
        FluidPersonRecord person = people.p[pr];
        float stick = (person.flags & FLUID_PERSON_STICK_MASK) == 0u ? person.stickiness : 0.0;
        for (uint k = 0u; k < person.proxyCount; k++)
        {
            uint pi = person.proxyStart + k;
            FluidProxyRecord P = proxies.p[pi];
            if (P.bone >= hdr.h.bones)
                continue;
            FluidBoneRecord B = bones.b[P.bone];
            vec4 r0 = mix(B.lastRow0, B.row0, t);
            vec4 r1 = mix(B.lastRow1, B.row1, t);
            vec4 r2 = mix(B.lastRow2, B.row2, t);
            float sd;
            vec3 nrm;
            proxyShape(P, r0, r1, r2, p, sd, nrm);
            if (sd > r + band)
                continue;
            float depth = max(r - sd, 0.0);
            if (sd < r)
                p += nrm * (r - sd);
            vec3 surface = p - nrm * max(sd, r);
            vec4 q0 = mix(B.lastRow0, B.row0, tPrev);
            vec4 q1 = mix(B.lastRow1, B.row1, tPrev);
            vec4 q2 = mix(B.lastRow2, B.row2, tPrev);
            vec3 local = fluidAffineInverse(r0, r1, r2, surface);
            vec3 vSurf = (surface - fluidAffine(q0, q1, q2, local)) / max(dt, 1e-9);
            consider(best, max(sd, r), nrm, surface, vSurf, fluidTargetKey(FLUID_ANCHOR_BONE, pi), FLUID_SKIN_FRICTION, stick, 1.0, depth, FLUID_SKIN_BOUNCE);
            if (lastSubstep && P.part < uint(FLUID_BODY_PARTS))
            {
                uint s = FLUID_S_PARTS + (pr * uint(FLUID_BODY_PARTS) + P.part) * 4u;
                atomicAdd(stats.v[s], 1u);
                atomicMax(stats.v[s + 1u], floatBitsToUint(depth));
                atomicMax(stats.v[s + 2u], floatBitsToUint(length(vSurf)));
                // the deepest contact's substance rides under its depth bits (wash needs to know water touched)
                atomicMax(stats.v[s + 3u], (floatBitsToUint(depth) & 0xFFFFFF00u) | (substance & 255u));
            }
        }
    }
}

void collideSoft(vec3 x, inout vec3 p, float r, float band, float dt, float t, inout Contact best)
{
    uint n = hdr.h.softSpheres;
    for (uint k = 0u; k < n; k++)
    {
        FluidSoftSphere S = softSpheres.s[k];
        vec4 s = mix(S.last, S.now, t);
        vec3 d = p - s.xyz;
        float len = length(d);
        float sd = len - s.w;
        if (sd > r + band)
            continue;
        vec3 nrm = len > 1e-9 ? d / len : vec3(0.0, 1.0, 0.0);
        if (sd < r)
            p += nrm * (r - sd);
        consider(best, max(sd, r), nrm, s.xyz + nrm * s.w, (S.now.xyz - S.last.xyz) / max(hdr.h.dtFrame, 1e-9), 0u, 1.0, 0.0, 0.5, max(r - sd, 0.0), 0.0);
    }
}

void collidePrediction(uint i)
{
    bool inRange = i < counters.v[FLUID_C_AWAKE];
    uint slot = inRange ? i : 0u;
    FluidParticle P = pa.p[slot];
    FluidParticle prediction = pb.p[slot];
    FluidSubstanceRecord sub = substances.s[inRange ? fluidSubstanceOf(P.material) : 0u];
    uint id = inRange ? idA.v[i] : 0u;
    float h = sub.smoothingRadius;
    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    float tPrev = float(pc.c.substep) / n;
    float dt = hdr.h.dtSubstep;
    bool lastSubstep = pc.c.substep + 1u >= hdr.h.substeps;
    uint f = fluidFrameOf(P.material);
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, t, fq, fpos, fscale);
    bool inFrame = f != 0u && f < hdr.h.frames;
    vec3 x = P.position;
    vec3 p = prediction.position;
    vec3 v = prediction.velocity;

    // Collision in world space: particles in a container's frame go out through the frame's pose and back.
    vec4 fqp;
    vec3 fposp;
    float fscalep;
    framePose(f, tPrev, fqp, fposp, fscalep);
    vec3 xw = inFrame ? fluidToWorld(x, fqp, fposp, fscalep) : x;
    vec3 pw = inFrame ? fluidToWorld(p, fq, fpos, fscale) : p;
    vec3 pwPredicted = pw;
    float r = 0.5 * sub.spacing;
    float band = sub.stickDistance + FLUID_CONTACT_SLOP * sub.spacing;
    Contact best;
    best.distance = 1e30;
    best.normal = vec3(0.0);
    best.point = vec3(0.0);
    best.velocity = vec3(0.0);
    best.key = 0u;
    best.friction = 0.0;
    best.stickiness = 0.0;
    best.wetting = 0.0;
    best.depth = 0.0;
    best.bounce = 0.0;

    uint skipPerson = FLUID_NONE;
    uint akind = anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK;
    if (akind == FLUID_ANCHOR_BONE || akind == FLUID_ANCHOR_SKIN)
        skipPerson = anchors.a[id].target >> 8;

    collideColliders(xw, pw, r, band, P.mass, dt, t, tPrev, lastSubstep, inRange, best);
    if (!inRange)
        return;
    collideProxies(xw, pw, r, band, dt, t, tPrev, skipPerson, lastSubstep, fluidSubstanceOf(P.material), best);
    collideSoft(xw, pw, r, band, dt, t, best);

    // A contact never sends a particle off faster than it came in: water left a head at the 6 m/s clamp from a
    // 2.4 m/s hit. The push-out is a position fix, and whatever of it exceeds the particle's own approach to the
    // surface (it started inside, or the surface moved onto it faster than it) moves the previous position as well,
    // so the velocity Update derives keeps at most the contact's Bounce of the approach along the normal, relative to
    // the surface.
    if (best.distance < 1e29 && best.depth > 0.0)
    {
        float dtC = max(dt, 1e-9);
        float approach = -dot((pwPredicted - xw) / dtC - best.velocity, best.normal);
        float allowed = best.bounce * max(approach, 0.0);
        float leaving = dot((pw - xw) / dtC - best.velocity, best.normal);
        if (leaving > allowed)
        {
            xw += best.normal * ((leaving - allowed) * dtC);
            x = inFrame ? fluidToLocal(xw, fqp, fposp, fscalep) : xw;
        }
    }

    // Wetting (Akinci adhesion): a particle near a surface it wets is drawn in, never through.
    if (best.distance < h && sub.adhesion > 0.0)
    {
        float reach = max(h - r, 1e-6);
        float shape = clamp(1.0 - (best.distance - r) / reach, 0.0, 1.0);
        float accel = sub.adhesion * best.wetting * sub.surfaceTension * sub.spacing / max(P.mass, 1e-12) * shape;
        float pull = min(accel * dt * dt, max(best.distance - r, 0.0) * 0.5);
        pw -= best.normal * pull;
    }

    p = inFrame ? fluidToLocal(pw, fq, fpos, fscale) : pw;

    // The system's bounds hold its fluid like an invisible box.
    FluidSystemRecord S = systems.s[min(fluidSystemOf(P.material), FLUID_MAX_SYSTEMS - 1u)];
    if (any(greaterThan(S.boundsHalf, vec3(0.0))))
    {
        vec3 lo = S.boundsCenter - S.boundsHalf + r;
        vec3 hi = S.boundsCenter + S.boundsHalf - r;
        p = clamp(p, lo, hi);
    }

    FluidParticle Q = P;
    Q.position = p;
    Q.previous = x;
    Q.velocity = v;
    pb.p[i] = Q;

    uint b = i * FLUID_SLOT_STRIDE;
    if (best.distance < 1e29)
    {
        vec3 nl = inFrame ? normalize(fluidQuatInverseRotate(fq, best.normal)) : best.normal;
        vec3 sl = inFrame ? fluidToLocal(best.point, fq, fpos, fscale) : best.point;
        vec3 vl = inFrame ? fluidQuatInverseRotate(fq, best.velocity) / fscale : best.velocity;
        slots.v[b + 1u] = vec4(nl, dot(sl, nl));
        slots.v[b + 2u] = vec4(vl, uintBitsToFloat(best.key));
        slots.v[b + 3u] = vec4(best.friction, best.stickiness, best.distance, 0.0);
        slots.v[b].w = best.depth;
    }
    else
    {
        slots.v[b + 1u] = vec4(0.0);
        slots.v[b + 2u] = vec4(0.0);
        slots.v[b + 3u] = vec4(0.0, 0.0, 1e30, 0.0);
        slots.v[b].w = 0.0;
    }
}

void main()
{
    // Neighbour reductions use several lanes; collision work needs one active lane per particle. -xlinka
    if (pc.c.pass == 1u)
    {
        uint particle = gl_GlobalInvocationID.x;
        // Inactive lanes still load collider tiles and reach both barriers. -xlinka
        collidePrediction(particle);
        return;
    }
    uint gid = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    uint i = gid / FLUID_LANES;
    uint lane = gid % FLUID_LANES;
    uint awake = counters.v[FLUID_C_AWAKE];
    bool inRange = i < awake;
    uint si = inRange ? i : 0u;

    FluidParticle P = pa.p[si];
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float h = sub.smoothingRadius;
    float spiky = fluidSpikyCoefficient(h);
    uint id = idA.v[si];
    vec3 x = P.position;
    vec3 ni = aux.v[id * FLUID_AUX_STRIDE].xyz;
    uint count = inRange ? floatBitsToUint(slots.v[si * FLUID_SLOT_STRIDE].z) : 0u;

    vec3 coh = vec3(0.0);
    vec3 curv = vec3(0.0);
    vec3 eta = vec3(0.0);
    for (uint e = lane; e < count; e += FLUID_LANES)
    {
        uint j = lists.v[si * FLUID_MAX_NEIGHBOURS + e];
        vec3 rv = x - pa.p[j].position;
        float len = length(rv);
        if (len >= h || len <= 1e-9)
            continue;
        uint idj = idA.v[j];
        coh += pa.p[j].mass * fluidCohesionKernel(len, h) * (rv / len);
        curv += ni - aux.v[idj * FLUID_AUX_STRIDE].xyz;
        eta += pa.p[j].volume * length(aux.v[idj * FLUID_AUX_STRIDE + 1u].xyz) * fluidSpikyGrad(rv, len, h, spiky);
    }
    sA[lid] = vec4(coh, 0.0);
    sB[lid] = vec4(curv, 0.0);
    sC[lid] = vec4(eta, 0.0);
    reduceLanes(lid, lane);
    if (!inRange || lane != 0u)
        return;

    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    float tPrev = float(pc.c.substep) / n;
    float dt = hdr.h.dtSubstep;
    bool lastSubstep = pc.c.substep + 1u >= hdr.h.substeps;
    uint phase = fluidPhaseOf(P.phase);
    uint f = fluidFrameOf(P.material);
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, t, fq, fpos, fscale);
    bool inFrame = f != 0u && f < hdr.h.frames;

    vec3 a = fluidQuatInverseRotate(fq, hdr.h.gravity) / fscale;
    if (inFrame)
    {
        FluidFrameRecord F = frames.f[f];
        vec3 al = fluidQuatInverseRotate(fq, F.linearAcceleration) / fscale;
        vec3 w = fluidQuatInverseRotate(fq, F.angularVelocity);
        vec3 aw = fluidQuatInverseRotate(fq, F.angularAcceleration);
        a += -al - cross(aw, x) - 2.0 * cross(w, P.velocity) - cross(w, cross(w, x));
    }
    float cohesionScale = phase == FLUID_PHASE_STRAND ? 1.0 / max(float(count), 1.0) : 1.0;
    a += -sub.cohesion * (sA[lid].xyz + sB[lid].xyz) * cohesionScale;
    vec3 omega = aux.v[id * FLUID_AUX_STRIDE + 1u].xyz;
    vec3 etaV = sC[lid].xyz;
    if (sub.viscousPasses == 0u && dot(etaV, etaV) > 1e-20 && dot(omega, omega) > 1e-12)
        a += VORTICITY_STRENGTH * sub.spacing * length(omega) * cross(normalize(etaV), omega);

    vec3 v = P.velocity + a * dt;
    float speed = length(v);
    if (speed > sub.maxSpeed)
        v *= sub.maxSpeed / speed;
    vec3 p = x + v * dt;

    FluidParticle prediction = P;
    prediction.position = p;
    prediction.previous = x;
    prediction.velocity = v;
    pb.p[i] = prediction;
}
