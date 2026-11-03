// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Substep, kernel 4 of 4: velocity from the corrected position, then what needs it. Lanes gather XSPH, the
// vorticity and the Akinci surface normal (both kept by id for the next predict) and the colour diffusion; lane 0
// applies them, creeps each bond's rest length past yield (Herschel-Bulkley), tears bulk bonds past StretchLimit,
// drops bonds to particles that are gone, and breaks, creeps or re-seats the anchor. Both ends of a bond see the
// same positions and the same rest length, so they creep and tear it identically without talking. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"
#include "Include/FluidThinning.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_DECLARE_PA
FLUID_DECLARE_PB
FLUID_DECLARE_IDA
FLUID_DECLARE_ID_TO_SLOT
FLUID_DECLARE_BONDS
FLUID_DECLARE_ANCHORS
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_AUX
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
shared vec4 sC[FLUID_GROUP_SIZE];
shared vec4 sD[FLUID_GROUP_SIZE];
shared vec4 sE[FLUID_GROUP_SIZE];

// colour diffusion per second at ColorMixing 1 in a runny fluid; thicker fluids divide it by (1 + mu)
#define COLOR_RATE 0.6
// a bulk bond whose rest length crept past this many spacings lets go
#define BULK_REST_LIMIT 2.0
// m/s^2: the most speed the substep's position corrections may add, per second. A correction is a position fix,
// and divided by the substep's dt a fixed fix is a speed that grows with the frame rate: an overlap the solver
// resolved over a few substeps at 800 fps fired particles off at the speed cap and through thin walls. Bounded as an
// acceleration, the fix still moves the particle in full and adds the same speed at any frame rate. The part of a fix
// that brakes the particle (against its own motion, at most its speed) counts in full, so a resting fluid's
// correction speed decays to zero: bounding the whole fix left settled water moving at exactly the bound (0.33 m/s at
// 90 Hz) and never asleep. Only what is left (turning it, or speeding it past a stop) is bounded; bounding the speed
// alone let a fix turn a 3 m/s fall sideways and through a floor at 800 fps.
#define MAX_FIX_ACCELERATION 60.0
// density neighbours at and above which a particle is deep in the bulk (a strand leaves its phase at the same count)
#define BULK_NEIGHBOURS 12.0

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

// The anchor's target as a pose at t: point maps a target-local point to world, so creep and re-seating can move
// the stored point. Skin anchors keep their barycentric and only break.
bool targetPose(FluidAnchor A, float t, out vec4 q, out vec3 position, out float scale, out vec4 r0, out vec4 r1, out vec4 r2)
{
    uint kind = A.kindAge & FLUID_ANCHOR_KIND_MASK;
    q = vec4(0.0, 0.0, 0.0, 1.0);
    position = vec3(0.0);
    scale = 1.0;
    r0 = vec4(1.0, 0.0, 0.0, 0.0);
    r1 = vec4(0.0, 1.0, 0.0, 0.0);
    r2 = vec4(0.0, 0.0, 1.0, 0.0);
    if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
    {
        if (A.target >= hdr.h.colliders)
            return false;
        FluidColliderRecord C = colliders.c[A.target];
        q = fluidQuatNlerp(C.lastRotation, C.rotation, t);
        position = mix(C.lastPosition, C.position, t);
        scale = mix(C.lastScale, C.scale, t);
        r0 = vec4(fluidQuatRotate(q, vec3(scale, 0.0, 0.0)).x, fluidQuatRotate(q, vec3(0.0, scale, 0.0)).x, fluidQuatRotate(q, vec3(0.0, 0.0, scale)).x, position.x);
        r1 = vec4(fluidQuatRotate(q, vec3(scale, 0.0, 0.0)).y, fluidQuatRotate(q, vec3(0.0, scale, 0.0)).y, fluidQuatRotate(q, vec3(0.0, 0.0, scale)).y, position.y);
        r2 = vec4(fluidQuatRotate(q, vec3(scale, 0.0, 0.0)).z, fluidQuatRotate(q, vec3(0.0, scale, 0.0)).z, fluidQuatRotate(q, vec3(0.0, 0.0, scale)).z, position.z);
        return true;
    }
    if (kind == FLUID_ANCHOR_BONE)
    {
        uint person = A.target >> 8;
        if (person >= hdr.h.people)
            return false;
        uint bi = people.p[person].boneStart + A.triangle;
        if (A.triangle >= people.p[person].boneCount || bi >= hdr.h.bones)
            return false;
        FluidBoneRecord B = bones.b[bi];
        r0 = mix(B.lastRow0, B.row0, t);
        r1 = mix(B.lastRow1, B.row1, t);
        r2 = mix(B.lastRow2, B.row2, t);
        return true;
    }
    return false;
}

bool skinPoint(FluidAnchor A, float t, out vec3 w, out vec3 normal, out vec3 a, out vec3 b, out vec3 c)
{
    w = vec3(0.0);
    normal = vec3(0.0, 1.0, 0.0);
    a = vec3(0.0);
    b = vec3(0.0);
    c = vec3(0.0);
    uint person = A.target >> 8;
    if (person >= hdr.h.people)
        return false;
    uint tri = people.p[person].triangleStart + A.triangle;
    if (A.triangle >= people.p[person].triangleCount || tri >= hdr.h.contactTriangles)
        return false;
    uint i0 = contactIndex.v[tri * 3u];
    uint i1 = contactIndex.v[tri * 3u + 1u];
    uint i2 = contactIndex.v[tri * 3u + 2u];
    a = mix(skinned.v[i0].lastPosition, skinned.v[i0].position, t);
    b = mix(skinned.v[i1].lastPosition, skinned.v[i1].position, t);
    c = mix(skinned.v[i2].lastPosition, skinned.v[i2].position, t);
    vec3 n = cross(b - a, c - a);
    n = length(n) > 1e-12 ? normalize(n) : vec3(0.0, 1.0, 0.0);
    w = a * (1.0 - A.point.x - A.point.y) + b * A.point.x + c * A.point.y + n * A.point.z;
    normal = n;
    return true;
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

    FluidParticle P = pa.p[si];
    FluidSubstanceRecord sub = substances.s[fluidSubstanceOf(P.material)];
    float h = sub.smoothingRadius;
    float h2 = h * h;
    float poly6 = fluidPoly6Coefficient(h);
    float spiky = fluidSpikyCoefficient(h);
    float dt = hdr.h.dtSubstep;
    float invDt = 1.0 / max(dt, 1e-9);
    vec3 pn = P.position;
    vec3 x = pb.p[si].previous;
    vec3 vPredicted = (pb.p[si].position - x) * invDt;
    vec3 dvFix = (pn - pb.p[si].position) * invDt;
    float before = length(vPredicted);
    vec3 u = before > 1e-9 ? vPredicted / before : vec3(0.0);
    vec3 brake = -u * clamp(-dot(dvFix, u), 0.0, before);
    vec3 rest_ = dvFix - brake;
    float restSpeed = length(rest_);
    float most = MAX_FIX_ACCELERATION * dt;
    vec3 v = vPredicted + brake + (restSpeed > most ? rest_ * (most / restSpeed) : rest_);
    vec4 tintI = fluidUnpackTint(P.tint);
    uint count = inRange ? floatBitsToUint(slots.v[si * FLUID_SLOT_STRIDE].z) : 0u;

    vec3 xsph = vec3(0.0);
    vec3 omega = vec3(0.0);
    vec3 normal = vec3(0.0);
    vec4 tintSum = vec4(0.0);
    float weightSum = 0.0;
    for (uint e = lane; e < count; e += FLUID_LANES)
    {
        uint j = lists.v[si * FLUID_MAX_NEIGHBOURS + e];
        vec3 xj = pa.p[j].position;
        vec3 r = pn - xj;
        float r2 = dot(r, r);
        if (r2 >= h2)
            continue;
        float len = sqrt(r2);
        vec3 vj = j < awake ? (xj - pb.p[j].previous) * invDt : vec3(0.0);
        float vol = pa.p[j].volume;
        float w = vol * fluidPoly6(r2, h2, poly6);
        vec3 grad = fluidSpikyGrad(r, len, h, spiky);
        xsph += w * (vj - v);
        omega += vol * cross(vj - v, grad);
        normal += vol * grad;
        tintSum += w * (fluidUnpackTint(pa.p[j].tint) - tintI);
        weightSum += w;
    }
    sA[lid] = vec4(xsph, weightSum);
    sB[lid] = vec4(omega, 0.0);
    sC[lid] = vec4(normal, 0.0);
    sD[lid] = tintSum;
    barrier();
    for (uint off = FLUID_LANES / 2u; off > 0u; off >>= 1)
    {
        if (lane < off)
        {
            sA[lid] += sA[lid + off];
            sB[lid] += sB[lid + off];
            sC[lid] += sC[lid + off];
            sD[lid] += sD[lid + off];
        }
        barrier();
    }
    if (!inRange || lane != 0u)
        return;

    uint id = idA.v[i];
    // An anchored particle moves with what holds it; smoothing it toward the bulk behind would drag it off its
    // target. A strand takes the full viscous smoothing even in a yielding network, whose bulk leaves viscosity to
    // its bonds: a thread has no bulk around it, and undamped it whipped, over-refined and pinched early.
    bool strandPhase = fluidPhaseOf(P.phase) == FLUID_PHASE_STRAND;
    float xsphRate = strandPhase ? max(sub.xsphFactor, sub.viscosity / (sub.density * sub.spacing * sub.spacing)) : sub.xsphFactor;
    if ((anchors.a[id].kindAge & FLUID_ANCHOR_KIND_MASK) == FLUID_ANCHOR_NONE)
        v += fluidXsphStrength(xsphRate, dt) * sA[lid].xyz;
    aux.v[id * FLUID_AUX_STRIDE].xyz = h * sC[lid].xyz;
    aux.v[id * FLUID_AUX_STRIDE + 1u].xyz = sB[lid].xyz;

    // Colour: Fick-like mixing toward the neighbourhood, dithered by a seeded draw so 8-bit tints still drift.
    float weights = sA[lid].w;
    if (weights > 1e-9 && sub.colorMixing > 0.0)
    {
        float rate = clamp(sub.colorMixing * COLOR_RATE / (1.0 + sub.viscosity) * dt, 0.0, 0.5);
        vec4 mixed = clamp(tintI + rate * sD[lid] / weights, 0.0, 1.0);
        vec4 noise = vec4(fluidRandom(id, hdr.h.sequence * 8u + pc.c.substep), fluidRandom(id + 1u, hdr.h.sequence * 8u + pc.c.substep),
                          fluidRandom(id + 2u, hdr.h.sequence * 8u + pc.c.substep), fluidRandom(id + 3u, hdr.h.sequence * 8u + pc.c.substep));
        uvec4 q = uvec4(clamp(floor(mixed * 255.0 + noise), 0.0, 255.0));
        pa.p[i].tint = q.x | (q.y << 8) | (q.z << 16) | (q.w << 24);
    }

    // The anchor's pose this substep, first: the bond loop needs its normal for the peel front.
    FluidAnchor A = anchors.a[id];
    uint kind = A.kindAge & FLUID_ANCHOR_KIND_MASK;
    float n = float(max(hdr.h.substeps, 1u));
    float t = float(pc.c.substep + 1u) / n;
    uint f = fluidFrameOf(P.material);
    bool inFrame = f != 0u && f < hdr.h.frames;
    vec4 fq;
    vec3 fpos;
    float fscale;
    framePose(f, t, fq, fpos, fscale);
    vec3 pw = inFrame ? fluidToWorld(pn, fq, fpos, fscale) : pn;
    vec4 tq;
    vec3 tpos;
    float tscale;
    vec4 r0, r1, r2;
    bool posed = false;
    bool valid = false;
    vec3 target = vec3(0.0);
    vec3 anchorNormal = vec3(0.0);
    vec3 ta = vec3(0.0), tb = vec3(0.0), tc = vec3(0.0);
    if (kind != FLUID_ANCHOR_NONE)
    {
        posed = targetPose(A, t, tq, tpos, tscale, r0, r1, r2);
        valid = posed ? true : (kind == FLUID_ANCHOR_SKIN && skinPoint(A, t, target, anchorNormal, ta, tb, tc));
        if (posed)
        {
            target = fluidAffine(r0, r1, r2, A.point);
            anchorNormal = normalize(fluidAffineVector(r0, r1, r2, fluidUnpackNormal(A.normal)));
        }
        if (inFrame)
            anchorNormal = fluidQuatInverseRotate(fq, anchorNormal);
    }

    // Bonds this end holds. Clavet's re-forming (below) is for the deep bulk only.
    // A moving coat is a thread root, even when its surface layer still has many neighbours. -xlinka
    bool deep = kind == FLUID_ANCHOR_STATIC || (kind == FLUID_ANCHOR_NONE && aux.v[id * FLUID_AUX_STRIDE].w >= BULK_NEIGHBOURS);
    uint slotCount = hdr.h.bondSlots;
    bool strand = fluidPhaseOf(P.phase) == FLUID_PHASE_STRAND;
    float cured = sub.cureTime > 0.0 ? clamp(P.age / sub.cureTime, 0.0, 1.0) : 0.0;
    float bonded = 0.0;
    float bondedAnchored = 0.0;
    for (uint k = 0u; k < slotCount; k++)
    {
        uint at = id * slotCount + k;
        FluidBond bnd = bonds.b[at];
        if (bnd.partner == FLUID_NONE)
            continue;
        uint js = idToSlot.v[bnd.partner];
        if (js == FLUID_NONE || fluidPhaseOf(pa.p[js].phase) == FLUID_PHASE_DEAD)
        {
            bonds.b[at].partner = FLUID_NONE;
            continue;
        }
        float len = distance(pn, pa.p[js].position);
        float strain = len / max(bnd.restLength, 1e-9) - 1.0;
        bool partnerStrand = fluidPhaseOf(pa.p[js].phase) == FLUID_PHASE_STRAND;
        // A bulk bond goes when it tears, or when yield creep has grown its rest length past the reach a new bond
        // forms in (Clavet's rule): the network then re-forms around where the particles are, which is what lets a
        // neck flow thinner instead of stretching as a ladder. Strand links are exempt; they refine and snap.
        // Both ends decide it alike, each needing both deep (held by a static surface counts: the floor is a puddle's
        // boundary). Deciding it from its own end only, a deep particle let go of its copy while a coat particle kept
        // the other, and that one-sided bond pulled the coat toward the puddle and was never thinned or snapped.
        uint partnerKind = anchors.a[bnd.partner].kindAge & FLUID_ANCHOR_KIND_MASK;
        bool partnerDeep = partnerKind == FLUID_ANCHOR_STATIC
            || (partnerKind == FLUID_ANCHOR_NONE && aux.v[bnd.partner * FLUID_AUX_STRIDE].w >= BULK_NEIGHBOURS);
        // Sparse liquid on one static contact plane also re-forms its network as it spreads. -xlinka
        uint contactBase = i * FLUID_SLOT_STRIDE;
        uint contactKey = floatBitsToUint(slots.v[contactBase + 2u].w);
        vec4 myPlane = slots.v[contactBase + 1u];
        vec4 hisPlane = js < awake ? slots.v[js * FLUID_SLOT_STRIDE + 1u] : vec4(0.0);
        float contactReach = (0.5 + FLUID_CONTACT_SLOP) * sub.spacing;
        bool sameFloor = js < awake && (contactKey >> FLUID_TARGET_KIND_SHIFT) == FLUID_ANCHOR_STATIC
            && contactKey == floatBitsToUint(slots.v[js * FLUID_SLOT_STRIDE + 2u].w)
            && dot(slots.v[contactBase + 1u].xyz, slots.v[js * FLUID_SLOT_STRIDE + 1u].xyz) > 0.9
            && abs(dot(pn, myPlane.xyz) - myPlane.w) <= contactReach
            && abs(dot(pa.p[js].position, hisPlane.xyz) - hisPlane.w) <= contactReach;
        // A ligament can have two bulk roots; its existing strand flag still delegates failure to frame end. -xlinka
        bool strandLink = (bnd.strainFlags & FLUID_BOND_STRAND) != 0u;
        if (!strandLink && !strand && !partnerStrand && (strain > sub.stretchLimit
            || bnd.restLength > BULK_REST_LIMIT * sub.spacing && ((deep && partnerDeep) || sameFloor)))
        {
            bonds.b[at].partner = FLUID_NONE;
            continue;
        }
        if (sub.creepTime > 0.0 && cured < 1.0)
        {
            float excess = abs(strain) - sub.yieldStrain;
            if (excess > 0.0)
            {
                // Herschel-Bulkley: shear rate = ((stress - yield) / K)^(1/n) with stress = G strain, so in strain
                // and creep time K / G the rate is (excess / creepTime)^(1/n). Raised to n instead, a shear-thinning
                // kind (slime, n 0.5) stiffened with speed and out-pulled every anchor holding it.
                float rate = pow(excess / sub.creepTime, 1.0 / max(sub.flowIndex, 0.1)) * (1.0 - cured);
                // Elastic strands recoil after a snap; a purely viscous thread must keep flowing. -xlinka
                bool slackPiece = sub.relaxation > 0.0 && ((P.phase | pa.p[js].phase) & FLUID_PARTICLE_SLACK) != 0u;
                float rest = bnd.restLength * (1.0 + sign(strain) * min(rate * dt, 0.5) * (slackPiece && strain > 0.0 ? 0.0 : 1.0));
                // A viscous network's elasticity only stands in for viscosity under a slow load: past a small strain
                // it flows at once, so an impact is absorbed instead of stored. Held to its rest shape through a
                // pour, a honey puddle sloshed like jelly for seconds.
                if ((sub.flags & FLUID_SUBSTANCE_STIFF) != 0u && abs(strain) > FLUID_PLASTIC_STRAIN && !strand && !partnerStrand)
                    rest = len / (1.0 + sign(strain) * FLUID_PLASTIC_STRAIN);
                // a bulk bond creeps no shorter than density lets its ends sit (see frame end B); a strand's links
                // refine shorter than that
                bonds.b[at].restLength = max(rest, (strand || partnerStrand || (sub.flags & FLUID_SUBSTANCE_STIFF) == 0u ? 0.25 : FLUID_BOND_REST_MIN) * sub.spacing);
            }
        }
        // Peel counts the bonds along the held surface only: a neighbour beside this one that lets go is the
        // peel front reaching it. The bulk behind the stuck layer is never anchored, and counting it made every
        // anchor under a lifting foot start at half strength.
        vec3 dir = len > 1e-9 ? (pa.p[js].position - pn) / len : vec3(0.0);
        if (kind == FLUID_ANCHOR_NONE || abs(dot(dir, anchorNormal)) < 0.6)
        {
            bonded += 1.0;
            // the anchored bit frame end A set: a neighbour breaking its anchor in this same pass cannot race it
            if ((pa.p[js].phase & FLUID_PARTICLE_ANCHORED) != 0u)
                bondedAnchored += 1.0;
        }
    }

    // The anchor: break on force or gap, otherwise let its film creep (Bingham) and re-seat on stick-slip.
    if (kind != FLUID_ANCHOR_NONE)
    {
        bool frozen = (A.kindAge & FLUID_ANCHOR_FROZEN) != 0u;
        if (!valid)
        {
            if (!frozen)
                anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
        }
        else
        {
            float peel = bonded > 0.0 ? 0.4 + 0.6 * bondedAnchored / bonded : 1.0;
            float force = abs(slots.v[i * FLUID_SLOT_STRIDE + 3u].w) * invDt * invDt;
            vec3 off = pw - target;
            float gap = length(off);
            // Compression holds the film against the surface; tangential stress is handled by creep below.
            // Only traction away from the surface can peel an adhesive anchor. -xlinka
            vec3 offFrame = inFrame ? fluidQuatInverseRotate(fq, off) : off;
            force *= gap > 1e-9 ? max(dot(offFrame, anchorNormal) / gap, 0.0) : 0.0;
            if (!frozen && (force > A.strength * peel || gap > 1.5 * sub.spacing))
                anchors.a[id].kindAge = FLUID_ANCHOR_NONE;
            else if (posed && gap > 1e-9)
            {
                vec3 nw = normalize(fluidAffineVector(r0, r1, r2, fluidUnpackNormal(A.normal)));
                vec3 offT = off - nw * dot(off, nw);
                float tl = length(offT);
                vec3 moved = vec3(0.0);
                if (tl > 0.5 * sub.spacing)
                    moved = offT;
                else if (tl > 1e-9 && sub.anchorCompliance > 0.0 && sub.viscosity > 0.0)
                {
                    float area = FLUID_PI * 0.25 * sub.spacing * sub.spacing;
                    float stress = tl / sub.anchorCompliance / area;
                    float yieldStress = sub.yieldStress;
                    float slide = max(stress - yieldStress, 0.0) * sub.spacing / sub.viscosity * dt;
                    moved = offT * min(slide / tl, 1.0);
                }
                if (dot(moved, moved) > 0.0)
                {
                    vec3 point = fluidAffineInverse(r0, r1, r2, target + moved);
                    vec3 normal = fluidUnpackNormal(A.normal);
                    bool reseated = true;
                    if (kind == FLUID_ANCHOR_STATIC || kind == FLUID_ANCHOR_RIGID)
                    {
                        FluidColliderRecord C = colliders.c[A.target];
                        if ((C.kind != FLUID_COLLIDER_MESH && C.kind != FLUID_COLLIDER_CONVEX) || C.gridIndex == FLUID_NONE)
                        {
                            // Sliding must follow the finite surface, not its old infinite tangent plane. -xlinka
                            float radius = 0.5 * sub.spacing / max(tscale, 1e-6);
                            normal = primitiveNormal(C.kind, point, C.halfExtents, C.radius, 1e-4 * max(radius, 1e-4));
                            point += normal * (radius - primitiveSdf(C.kind, point, C.halfExtents, C.radius));
                            normal = primitiveNormal(C.kind, point, C.halfExtents, C.radius, 1e-4 * max(radius, 1e-4));
                        }
                        else
                        {
                            uint sb = i * FLUID_SLOT_STRIDE;
                            uint key = floatBitsToUint(slots.v[sb + 2u].w);
                            vec4 plane = slots.v[sb + 1u];
                            reseated = (key >> FLUID_TARGET_KIND_SHIFT) == kind && (key & FLUID_TARGET_INDEX_MASK) == A.target
                                && dot(plane.xyz, plane.xyz) > 0.5;
                            // Meshes use their current contact; without one, keep the last surface point. -xlinka
                            if (reseated)
                            {
                                vec3 next = inFrame ? fluidToLocal(target + moved, fq, fpos, fscale) : target + moved;
                                next += plane.xyz * (0.5 * sub.spacing - (dot(next, plane.xyz) - plane.w));
                                vec3 world = inFrame ? fluidToWorld(next, fq, fpos, fscale) : next;
                                point = fluidAffineInverse(r0, r1, r2, world);
                                vec3 worldNormal = inFrame ? fluidQuatRotate(fq, plane.xyz) : plane.xyz;
                                normal = normalize(fluidQuatInverseRotate(tq, worldNormal));
                            }
                        }
                    }
                    if (reseated)
                    {
                        anchors.a[id].point = point;
                        anchors.a[id].normal = fluidPackNormal(normal);
                    }
                }
            }
        }
    }

    float speed = length(v);
    if (speed > sub.maxSpeed)
        v *= sub.maxSpeed / speed;
    pa.p[i].velocity = v;
    pa.p[i].previous = x;
}
