// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Pass 0 makes one Jacobi pass over every constraint on a particle. Lanes gather the density correction
// and the near repulsion from the list and split the XPBD bonds; lane 0 adds bending (strands), averages them,
// over-relaxes and clamps, solves the anchor at pose(t) from there, then re-projects onto the substep's contact with static
// friction against the surface's own motion. Reads the predicted positions in B, writes the corrected position into
// A; nothing else in this pass reads an awake particle's A position, so the halves never race. Both ends of a bond
// compute the same correction from the same inputs and only the lower id stores its lambda. Pass 1 projects the
// result against primitive colliders with one thread per particle. -xlinka

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
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_HEADER
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_FRAMES
FLUID_DECLARE_COLLIDERS
FLUID_DECLARE_PEOPLE
FLUID_DECLARE_BONES
FLUID_DECLARE_CONTACT_INDEX
FLUID_DECLARE_SKINNED
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_PUSH

shared vec4 sA[FLUID_GROUP_SIZE];
shared vec4 sB[FLUID_GROUP_SIZE];

// the near repulsion reaches this far, in spacings: a lattice at rest never feels it
#define NEAR_REACH 0.6
// The bonds are one group, averaged among themselves and over-relaxed by this; density is another, at
// FLUID_DENSITY_SOR, and the two are added. Averaged in with a dozen bonds, density had a twelfth of its strength,
// and a bonded blob (oil, paint, honey, slime) fell through itself at g for its first five frames.
#define BOND_GROUP_SOR 1.0

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

// Where an anchor holds its particle at pose(t), in world space; false when its target is gone this frame.
bool anchorWorld(FluidAnchor A, float t, out vec3 w)
{
    uint kind = A.kindAge & FLUID_ANCHOR_KIND_MASK;
    w = vec3(0.0);
    if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
    {
        if (A.target >= hdr.h.colliders)
            return false;
        FluidColliderRecord C = colliders.c[A.target];
        w = fluidToWorld(A.point, fluidQuatNlerp(C.lastRotation, C.rotation, t), mix(C.lastPosition, C.position, t), mix(C.lastScale, C.scale, t));
        return true;
    }
    // People anchors name their person by slot and their bone or contact triangle relative to that person's
    // first one: the lists are rebuilt every frame and move whenever anyone's count changes.
    uint person = A.target >> 8;
    if (person >= hdr.h.people)
        return false;
    FluidPersonRecord R = people.p[person];
    if (kind == FLUID_ANCHOR_BONE)
    {
        uint bi = R.boneStart + A.triangle;
        if (A.triangle >= R.boneCount || bi >= hdr.h.bones)
            return false;
        FluidBoneRecord B = bones.b[bi];
        w = fluidAffine(mix(B.lastRow0, B.row0, t), mix(B.lastRow1, B.row1, t), mix(B.lastRow2, B.row2, t), A.point);
        return true;
    }
    if (kind == FLUID_ANCHOR_SKIN)
    {
        uint tri = R.triangleStart + A.triangle;
        if (A.triangle >= R.triangleCount || tri >= hdr.h.contactTriangles)
            return false;
        uint i0 = contactIndex.v[tri * 3u];
        uint i1 = contactIndex.v[tri * 3u + 1u];
        uint i2 = contactIndex.v[tri * 3u + 2u];
        vec3 a = mix(skinned.v[i0].lastPosition, skinned.v[i0].position, t);
        vec3 b = mix(skinned.v[i1].lastPosition, skinned.v[i1].position, t);
        vec3 c = mix(skinned.v[i2].lastPosition, skinned.v[i2].position, t);
        vec3 n = cross(b - a, c - a);
        n = length(n) > 1e-12 ? normalize(n) : vec3(0.0, 1.0, 0.0);
        w = a * (1.0 - A.point.x - A.point.y) + b * A.point.x + c * A.point.y + n * A.point.z;
        return true;
    }
    return false;
}

void projectColliders(uint i)
{
    FluidParticle P = pb.p[i];
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float d = sub.spacing;
    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    uint f = fluidFrameOf(P.material);
    bool inFrame = f != 0u && f < hdr.h.frames;
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, t, fq, fpos, fscale);
    vec3 pn = pa.p[i].position;

    // The averaged correction can push a particle into a surface other than its nearest one (a tray's corner): one
    // more pass over the primitives puts it back outside all of them. Triangle grids keep their sweep in predict.
    if (hdr.h.colliders > 0u)
    {
        float r = 0.5 * d;
        vec3 pw = inFrame ? fluidToWorld(pn, fq, fpos, fscale) : pn;
        float tPrev = float(pc.c.substep) / n;
        vec4 fqp;
        vec3 fposp;
        float fscalep;
        framePose(f, tPrev, fqp, fposp, fscalep);
        vec3 xw = inFrame ? fluidToWorld(P.previous, fqp, fposp, fscalep) : P.previous;
        bool moved = false;
        for (uint c = 0u; c < hdr.h.colliders; c++)
        {
            FluidColliderRecord C = colliders.c[c];
            if ((C.flags & FLUID_COLLIDER_DRAIN) != 0u || ((C.kind == FLUID_COLLIDER_MESH || C.kind == FLUID_COLLIDER_CONVEX) && C.gridIndex != FLUID_NONE))
                continue;
            vec3 cp = mix(C.lastPosition, C.position, t);
            float cs = max(mix(C.lastScale, C.scale, t), 1e-6);
            float reach = (dot(abs(C.halfExtents), vec3(1.0)) + abs(C.radius)) * cs + r;
            // This enclosing box only skips endpoints the primitive projection already leaves untouched. -xlinka
            if (any(greaterThan(abs(pw - cp), vec3(reach + 1e-5 * max(reach, 1.0)))))
                continue;
            vec4 cq = fluidQuatNlerp(C.lastRotation, C.rotation, t);
            vec3 lp = fluidToLocal(pw, cq, cp, cs);
            uint kind = C.kind == FLUID_COLLIDER_CONVEX ? FLUID_COLLIDER_BOX : C.kind;
            float rl = r / cs;
            float sd = primitiveSdf(kind, lp, C.halfExtents, C.radius);
            if (sd >= rl)
                continue;
            vec3 lx = fluidToLocal(xw, fluidQuatNlerp(C.lastRotation, C.rotation, tPrev), mix(C.lastPosition, C.position, tPrev),
                max(mix(C.lastScale, C.scale, tPrev), 1e-6));
            float sx = primitiveSdf(kind, lx, C.halfExtents, C.radius);
            if (sx >= 0.5 * rl && sd < 0.5 * rl)
            {
                // started outside: back along the path to where it first touched
                float lo = 0.0, hi = 1.0;
                for (int s = 0; s < 8; s++)
                {
                    float mid = 0.5 * (lo + hi);
                    if (primitiveSdf(kind, mix(lx, lp, mid), C.halfExtents, C.radius) < rl)
                        hi = mid;
                    else
                        lo = mid;
                }
                lp = mix(lx, lp, lo);
                sd = primitiveSdf(kind, lp, C.halfExtents, C.radius);
            }
            else if (sd < -rl)
                continue;
            vec3 nrm = primitiveNormal(kind, lp, C.halfExtents, C.radius, 1e-4 * max(rl, 1e-4));
            lp += nrm * (rl - sd);
            pw = fluidToWorld(lp, cq, cp, cs);
            moved = true;
        }
        if (moved)
            pn = inFrame ? fluidToLocal(pw, fq, fpos, fscale) : pw;
    }
    pa.p[i].position = pn;
}

void main()
{
    if (pc.c.pass == 1u)
    {
        uint particle = gl_GlobalInvocationID.x;
        if (particle < counters.v[FLUID_C_AWAKE]) projectColliders(particle);
        return;
    }
    uint gid = gl_GlobalInvocationID.x;
    uint lid = gl_LocalInvocationIndex;
    uint i = gid / FLUID_LANES;
    uint lane = gid % FLUID_LANES;
    uint awake = counters.v[FLUID_C_AWAKE];
    bool inRange = i < awake;
    uint si = inRange ? i : 0u;

    FluidParticle P = pb.p[si];
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float h = sub.smoothingRadius;
    float h2 = h * h;
    float poly6 = fluidPoly6Coefficient(h);
    float spiky = fluidSpikyCoefficient(h);
    float restFill = sub.restDensity;
    float d = sub.spacing;
    float dt = hdr.h.dtSubstep;
    vec3 p = P.position;
    uint id = idA.v[si];
    bool strand = fluidPhaseOf(P.phase) == FLUID_PHASE_STRAND;
    uint count = inRange ? floatBitsToUint(slots.v[si * FLUID_SLOT_STRIDE].z) : 0u;
    float lambdaI = slots.v[si * FLUID_SLOT_STRIDE].x;
    float wI = 1.0 / max(P.mass, 1e-12);

    vec3 dp = vec3(0.0);
    float terms = 0.0;
    if (!strand)
    {
        float nearReach = NEAR_REACH * d;
        for (uint e = lane; e < count; e += FLUID_LANES)
        {
            uint j = lists.v[si * FLUID_MAX_NEIGHBOURS + e];
            vec3 xj = j < awake ? pb.p[j].position : pa.p[j].position;
            vec3 r = p - xj;
            float r2 = dot(r, r);
            if (r2 >= h2)
                continue;
            float len = sqrt(r2);
            float lambdaJ = j < awake ? slots.v[j * FLUID_SLOT_STRIDE].x : 0.0;
            dp += fluidDensityTerm(lambdaI, lambdaJ, pa.p[j].volume, restFill, r, len, h, h2, poly6, spiky);
            if (sub.nearRepulsion > 0.0 && len < nearReach && len > 1e-9)
            {
                float q = 1.0 - len / nearReach;
                dp += sub.nearRepulsion * d * q * q * (r / len);
            }
        }
    }

    // Bonds: lanes take slots lane, lane + 8. A bond to a particle that died or went to sleep holds it still.
    uint slotCount = hdr.h.bondSlots;
    float cured = sub.cureTime > 0.0 ? clamp(P.age / sub.cureTime, 0.0, 1.0) : 0.0;
    float compliance = sub.bondCompliance * (1.0 - 0.9 * cured);
    float bondTerms = 0.0;
    vec3 dpBond = vec3(0.0);
    for (uint k = lane; inRange && k < slotCount; k += FLUID_LANES)
    {
        FluidBond bnd = bonds.b[id * slotCount + k];
        if (bnd.partner == FLUID_NONE)
            continue;
        uint js = idToSlot.v[bnd.partner];
        if (js == FLUID_NONE || fluidPhaseOf(pa.p[js].phase) == FLUID_PHASE_DEAD)
            continue;
        bool partnerAwake = js < awake;
        vec3 xj = partnerAwake ? pb.p[js].position : pa.p[js].position;
        float wJ = partnerAwake ? 1.0 / max(pa.p[js].mass, 1e-12) : 0.0;
        // A bond crept longer than a spacing stands for a column drawn thinner (its goo over a longer length), so
        // its compliance grows with (rest / d)^2. At 1/(G d) whatever its length, a column drawn to five spacings
        // pulled 25 times too hard: it held every anchor under a lifting sole and no ligament ever weakened as it
        // stretched.
        float stretched = max(bnd.restLength / d, 1.0);
        vec4 c = fluidBondCorrection(p - xj, bnd.restLength, 0.0, compliance * stretched * stretched, dt, wI, wJ);
        dpBond += c.xyz;
        bondTerms += 1.0;
        if (id < bnd.partner)
            bonds.b[id * slotCount + k].lambda = c.w;
    }
    sA[lid] = vec4(dp, bondTerms);
    sB[lid] = vec4(dpBond, 0.0);
    barrier();
    for (uint off = FLUID_LANES / 2u; off > 0u; off >>= 1)
    {
        if (lane < off)
        {
            sA[lid] += sA[lid + off];
            sB[lid] += sB[lid + off];
        }
        barrier();
    }
    if (!inRange || lane != 0u)
        return;

    vec3 densitySum = sA[lid].xyz;
    vec3 sum = sB[lid].xyz;
    terms = sA[lid].w;
    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    uint f = fluidFrameOf(P.material);
    bool inFrame = f != 0u && f < hdr.h.frames;
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, t, fq, fpos, fscale);

    // Bending on a strand's interior particles: pulled toward the midpoint of its two chain partners, so a falling
    // rope resists folding and buckles into coils under compression.
    if (strand && sub.bendCompliance > 0.0)
    {
        vec3 ends[2];
        uint found = 0u;
        for (uint k = 0u; k < slotCount && found < 2u; k++)
        {
            uint partner = bonds.b[id * slotCount + k].partner;
            if (partner == FLUID_NONE)
                continue;
            uint js = idToSlot.v[partner];
            if (js == FLUID_NONE)
                continue;
            ends[found++] = js < awake ? pb.p[js].position : pa.p[js].position;
        }
        if (found == 2u)
        {
            vec3 mid = 0.5 * (ends[0] + ends[1]);
            vec3 off = p - mid;
            float c = length(off);
            if (c > 1e-9)
            {
                float alpha = sub.bendCompliance / (dt * dt);
                float dl = -c / (wI + alpha);
                sum += wI * dl * (off / c);
                terms += 1.0;
            }
        }
    }

    // Two groups added: density at its own weight, the bonds and bending averaged among themselves. A strand has
    // no density term, so its links are all it has.
    vec3 pn;
    if (strand)
        pn = p + fluidAverage(sum, terms, d);
    else
    {
        vec3 step_ = densitySum * (sub.bondCompliance > 0.0 ? FLUID_DENSITY_SOR : FLUID_DENSITY_SOR_LIQUID)
            + (terms > 0.0 ? sum * (BOND_GROUP_SOR / terms) : vec3(0.0));
        pn = p + fluidAverage(step_ / FLUID_SOR, 1.0, d);
    }

    // The anchor is solved after the average, from where the others left the particle: averaged in, it was diluted
    // by the particle's bond count (a dozen in a slime puddle), lagged its target by more than its own StickDistance
    // and broke on the first frame of any lift. Its target is kinematic, so solving it last stays local and
    // race-free.
    float anchorLambda = 0.0;
    FluidAnchor A = anchors.a[id];
    if ((A.kindAge & FLUID_ANCHOR_KIND_MASK) != FLUID_ANCHOR_NONE)
    {
        vec3 w;
        if (anchorWorld(A, t, w))
        {
            vec3 target = inFrame ? fluidToLocal(w, fq, fpos, fscale) : w;
            // A strand's end keeps the averaged share: its one or two links are all it has, and pinning it at
            // full weight stretched every held thread into more refinement than its volume could feed.
            vec4 c = fluidAnchorCorrection((strand ? p : pn) - target, 0.0, sub.anchorCompliance, dt, wI);
            pn += strand ? fluidAverage(c.xyz, terms + 1.0, d) : fluidAverage(c.xyz / FLUID_SOR, 1.0, d);
            anchorLambda = c.w;
        }
    }

    // Re-project onto the substep's contact plane, then position-level friction against the surface's motion:
    // static while the slip is under mu times the push, kinetic past it.
    uint b = i * FLUID_SLOT_STRIDE;
    vec4 plane = slots.v[b + 1u];
    if (dot(plane.xyz, plane.xyz) > 0.5)
    {
        float r = 0.5 * d;
        float s = dot(pn, plane.xyz) - plane.w;
        float push = max(r - s, 0.0);
        pn += plane.xyz * push;
        float pressed = push + slots.v[b].w;
        if (pressed > 0.0)
        {
            vec3 slip = (pn - P.previous) - slots.v[b + 2u].xyz * dt;
            vec3 tangent = slip - plane.xyz * dot(slip, plane.xyz);
            float tl = length(tangent);
            float mu = clamp(sub.friction * slots.v[b + 3u].x, 0.0, 2.0);
            if (tl > 1e-12)
                pn -= tl <= mu * pressed ? tangent : tangent * (mu * pressed / tl);
        }
    }
    slots.v[b + 3u].w = anchorLambda;
    pa.p[i].position = pn;
}
