// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Strand tubes, built per link. pc.pass picks the step:
//   0 build  per particle: draw every physical strand or ligament link once, including bulk-to-bulk roots.
//            Reciprocal links belong to their lower id; a one-sided root link belongs to its stored endpoint. -xlinka
//            Rings follow a distance-aware curve, with a minimum from tubeSegments and more for long links.
//            Shared particle rings meet exactly; a free end gets a hemisphere cap. The solver's radius forms the
//            neck, and smooth squared-radius profiles put the represented volume in each node's half-links and
//            cap. Polygon rings keep their physical cross-sectional area. Space is taken from a counter; what
//            does not fit is zeroed and counted. -xlinka
//   1 tail   per quad: zero what last frame drew past this frame's count.
// All live links are attempted. Only the allocated vertex/index bounds can defer a link; the overflow counter
// asks the renderer for more storage on the next readback.
// -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"
#include "Include/FluidSurface.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

FLUID_SURF_DECLARE_PUSH
FLUID_SURF_DECLARE_PARAMS
FLUID_SURF_DECLARE_PARTICLES
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_BONDS
FLUID_SURF_DECLARE_ACCUMULATORS
FLUID_SURF_DECLARE_TUBE_MESH
FLUID_SURF_DECLARE_LIVE

#include "Include/FluidSurfaceStrand.glsli"
#include "Include/FluidSurfaceCoat.glsli"
#include "Include/FluidSurfaceShape.glsli"

bool drawsEndpoint(uint i, FluidParticle p)
{
    if (i >= FLUID_SURF_LIVE || (sp.flags & FLUID_SURF_STRANDS) == 0u)
        return false;
    uint phase = fluidPhaseOf(p.phase);
    if (phase == FLUID_PHASE_DEAD || phase == FLUID_PHASE_SPRAY)
        return false;
    if (sp.system != FLUID_NONE && fluidSystemOf(p.material) != sp.system)
        return false;
    return !fluidSurfOriginHidden(sp, fluidOriginOf(p.material));
}

// Distance-aware tangents keep unevenly stretched links from looping past their neighbours. -xlinka
void curveTangents(vec3 p0, vec3 p1, vec3 p2, vec3 p3, out vec3 a, out vec3 b)
{
    float before = sqrt(max(length(p1 - p0), 1e-9));
    float middle = sqrt(max(length(p2 - p1), 1e-9));
    float after = sqrt(max(length(p3 - p2), 1e-9));
    a = middle * ((p1 - p0) / before - (p2 - p0) / (before + middle) + (p2 - p1) / middle);
    b = middle * ((p2 - p1) / middle - (p3 - p1) / (middle + after) + (p3 - p2) / after);
}

vec3 catmull(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t)
{
    vec3 a, b;
    curveTangents(p0, p1, p2, p3, a, b);
    float t2 = t * t;
    float t3 = t2 * t;
    return (2.0 * t3 - 3.0 * t2 + 1.0) * p1 + (t3 - 2.0 * t2 + t) * a
        + (-2.0 * t3 + 3.0 * t2) * p2 + (t3 - t2) * b;
}

vec3 catmullTangent(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t)
{
    vec3 a, b;
    curveTangents(p0, p1, p2, p3, a, b);
    if (t <= 0.0) return a;
    if (t >= 1.0) return b;
    float t2 = t * t;
    return (6.0 * t2 - 6.0 * t) * p1 + (3.0 * t2 - 4.0 * t + 1.0) * a
        + (-6.0 * t2 + 6.0 * t) * p2 + (3.0 * t2 - 2.0 * t) * b;
}

// Shared endpoints choose the same normal even when the link is traversed backwards. -xlinka
void ringFrame(vec3 tangent, out vec3 n, out vec3 b)
{
    vec3 magnitude = abs(tangent);
    float dominant = magnitude.x >= magnitude.y && magnitude.x >= magnitude.z ? tangent.x
        : (magnitude.y >= magnitude.z ? tangent.y : tangent.z);
    vec3 axis = dominant < 0.0 ? -tangent : tangent;
    float a = 1.0 / (1.0 + axis.z);
    n = vec3(1.0 - axis.x * axis.x * a, -axis.x * axis.y * a, -axis.x);
    b = cross(tangent, n);
}

vec3 curveDirection(vec3 p0, vec3 p1, vec3 p2, vec3 p3, float t)
{
    vec3 tangent = catmullTangent(p0, p1, p2, p3, t);
    float size = length(tangent);
    return size > 1e-9 ? tangent / size : normalize(p2 - p1 + vec3(1e-9));
}

// Carry a ring along the curve without choosing a fresh reference axis at every sample. -xlinka
vec3 transportNormal(vec3 from, vec3 to, vec3 normal)
{
    vec3 turn = cross(from, to);
    float facing = dot(from, to);
    vec3 carried = facing > -0.9999
        ? normal + cross(turn, normal) + cross(turn, cross(turn, normal)) / (1.0 + facing)
        : normal;
    carried -= to * dot(carried, to);
    if (dot(carried, carried) < 1e-12)
    {
        vec3 unused;
        ringFrame(to, carried, unused);
    }
    return normalize(carried);
}

float particleRadius(uint slot, float incomingLength)
{
    float lengthSum;
    uint a, b;
    uint chain = fluidSurfTubeChain(slot, particles[slot].position, lengthSum, a, b);
    return max(fluidSurfStrandRadius(fluidSurfTubeVolume(slot, particles[slot]), chain == 0u ? incomingLength : lengthSum, max(chain, 1u)), sp.minRadius);
}

// A thinned neck's displaced cross-section stays at its nodes. The two smooth half-links integrate to
// the same particle volume as the original constant-area allocation. -xlinka
float nodeRadius(uint slot, float baseRadius, float lengthSum, float incomingNeck)
{
    if (lengthSum <= 1e-9) return baseRadius;
    float neckSum = 0.0;
    uint count = 0u;
    uint base = fluidSurfBondBase(slot);
    for (uint k = 0u; k < sp.bondSlots; k++)
    {
        FluidBond bond = bonds[base + k];
        if (!fluidSurfIsChain(bond)) continue;
        uint other = fluidSurfSlotOf(bond.partner);
        if (other == FLUID_NONE || fluidSurfLinkInField(slot, other)) continue;
        float linkLength = length(particles[other].position - particles[slot].position);
        float limit = min(baseRadius, particleRadius(other, linkLength));
        float neck = bond.radius > 0.0 ? min(max(bond.radius, sp.minRadius), limit) : limit;
        neckSum += linkLength * neck * neck;
        count++;
    }
    // An incoming-only root still carries its half of the real link. -xlinka
    if (count == 0u)
    {
        neckSum = lengthSum * incomingNeck * incomingNeck;
        count = 1u;
    }
    float area = max(2.0 * baseRadius * baseRadius - neckSum / lengthSum, baseRadius * baseRadius);
    float radius = sqrt(area);
    if (count == 1u && !fluidSurfFieldOwner(slot, particles[slot]))
    {
        // Every terminal hemisphere uses this particle's volume too. -xlinka
        float cap = 8.0 / (3.0 * lengthSum);
        for (uint iteration = 0u; iteration < 5u; iteration++)
        {
            float square = radius * radius;
            radius -= (square * (1.0 + cap * radius) - area) / max(radius * (2.0 + 3.0 * cap * radius), 1e-12);
        }
    }
    return radius;
}

bool hasBacklink(uint slot, uint partner)
{
    uint base = fluidSurfBondBase(slot);
    for (uint k = 0u; k < sp.bondSlots; k++)
        if (bonds[base + k].partner == partner && fluidSurfIsChain(bonds[base + k]))
            return true;
    return false;
}

float neckProfile(float a, float neck, float b, float t, out float derivative)
{
    float u = t < 0.5 ? t * 2.0 : (t - 0.5) * 2.0;
    float start = t < 0.5 ? a * a : neck * neck;
    float end = t < 0.5 ? neck * neck : b * b;
    derivative = (end - start) * (12.0 * u * (1.0 - u));
    return sqrt(max(mix(start, end, u * u * (3.0 - 2.0 * u)), 0.0));
}

uint sides;
float polygonScale;
uint vertexBase;
bool wide;

void ringVertex(uint ring, uint s, vec3 centre, vec3 n, vec3 b, float radius, vec3 normalBend, vec4 tint, vec4 custom)
{
    float angle = 6.28318530718 * float(s) / float(sides);
    vec3 dir = cos(angle) * n + sin(angle) * b;
    vec3 normal = normalize(dir + normalBend);
    vec3 position = centre + dir * (radius * polygonScale);
    uint v = vertexBase + ring * sides + s;
    FLUID_WRITE_VERTEX(tvb, tab, sp.tubeVertexCapacity, sp.tubeNormalWords, v, position, normal, tint, custom)
}

void ringQuads(inout uint q, uint ringA, uint ringB)
{
    for (uint s = 0u; s < sides; s++)
    {
        uint s1 = (s + 1u) % sides;
        uint a0 = vertexBase + ringA * sides + s;
        uint a1 = vertexBase + ringA * sides + s1;
        uint b0 = vertexBase + ringB * sides + s;
        uint b1 = vertexBase + ringB * sides + s1;
        FLUID_WRITE_QUAD(tib, wide, q, a0, b0, b1, a1)
        q++;
    }
}

void buildPass()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= FLUID_SURF_LIVE)
        return;
    FluidParticle pa = particles[i];
    if (!drawsEndpoint(i, pa))
        return;
    float lenA;
    uint a0, a1;
    uint chainA = fluidSurfTubeChain(i, pa.position, lenA, a0, a1);
    if (chainA == 0u)
        return;
    float baseA = max(fluidSurfStrandRadius(fluidSurfTubeVolume(i, pa), lenA, chainA), sp.minRadius);
    float rA = nodeRadius(i, baseA, lenA, baseA);
    uint myId = fluidSurfKey(i);

    sides = clamp(sp.tubeSides, 3u, 64u);
    polygonScale = sqrt(6.28318530718 / (float(sides) * sin(6.28318530718 / float(sides))));
    wide = sp.tubeVertexCapacity > 65536u;
    uint minimumSegments = max(sp.tubeSegments, 2u);
    uint capSegments = max(minimumSegments, 2u);
    vec3 xa = fluidSurfDrawPosition(i, pa);

    uint base = fluidSurfBondBase(i);
    for (uint k = 0u; k < sp.bondSlots; k++)
    {
        FluidBond bond = bonds[base + k];
        if (!fluidSurfIsChain(bond))
            continue;
        uint j = fluidSurfSlotOf(bond.partner);
        if (j == FLUID_NONE)
            continue;
        FluidParticle pb = particles[j];
        if (!drawsEndpoint(j, pb) || fluidSurfLinkInField(i, j))
            continue;
        // One-sided root bonds still draw even when the other endpoint has the lower id. -xlinka
        if (bond.partner < myId && hasBacklink(j, myId))
            continue;

        float lenB;
        uint b0, b1;
        uint chainB = fluidSurfTubeChain(j, pb.position, lenB, b0, b1);
        if (chainB == 0u) lenB = distance(pa.position, pb.position);
        float baseB = max(fluidSurfStrandRadius(fluidSurfTubeVolume(j, pb), lenB, max(chainB, 1u)), sp.minRadius);
        float neckLimit = min(baseA, baseB);
        float neck = bond.radius > 0.0 ? min(max(bond.radius, sp.minRadius), neckLimit) : neckLimit;
        float rB = nodeRadius(j, baseB, lenB, neck);
        vec3 xb = fluidSurfDrawPosition(j, pb);
        uint prevSlot = a0 == j ? a1 : a0;
        uint nextSlot = b0 == i ? b1 : b0;
        vec3 x0 = prevSlot != FLUID_NONE && chainA == 2u ? fluidSurfDrawPosition(prevSlot, particles[prevSlot]) : 2.0 * xa - xb;
        vec3 x3 = nextSlot != FLUID_NONE && chainB == 2u ? fluidSurfDrawPosition(nextSlot, particles[nextSlot]) : 2.0 * xb - xa;
        // Bulk roots can be thinner than the field grid: close them even when no bulk surface covers the ring. -xlinka
        bool capA = chainA == 1u && !fluidSurfFieldOwner(i, pa);
        bool capB = chainB <= 1u && !fluidSurfFieldOwner(j, pb);
        float curveLength = 0.0;
        vec3 previousPoint = xa;
        for (uint pointIndex = 1u; pointIndex <= 4u; pointIndex++)
        {
            vec3 point = pointIndex == 4u ? xb : catmull(x0, xa, xb, x3, float(pointIndex) * 0.25);
            curveLength += length(point - previousPoint);
            previousPoint = point;
        }
        // Long ligaments get enough rings before refinement catches up. Storage grows on actual overflow. -xlinka
        uint segments = max(minimumSegments, uint(ceil(curveLength / max(sp.cellSize, 1e-6))));
        segments += segments & 1u;

        uint rings = segments + 1u + (capA ? capSegments : 0u) + (capB ? capSegments : 0u);
        uint nv = rings * sides;
        uint nq = (segments + (capA ? capSegments : 0u) + (capB ? capSegments : 0u)) * sides;
        uint v0 = atomicAdd(counters[FLUID_SC_TUBE_VERTS], nv);
        uint q0 = atomicAdd(counters[FLUID_SC_TUBE_QUADS], nq);
        if (v0 + nv > sp.tubeVertexCapacity || q0 + nq > sp.tubeQuadCapacity)
        {
            for (uint q = q0; q < min(q0 + nq, sp.tubeQuadCapacity); q++)
                FLUID_ZERO_QUAD(tib, wide, q)
            atomicAdd(counters[FLUID_SC_TUBE_OVERFLOW], 1u);
            continue;
        }
        vertexBase = v0;

        vec4 tintA = fluidUnpackTint(pa.tint);
        vec4 tintB = fluidUnpackTint(pb.tint);
        float speedA = length(fluidSurfDirToLocal(sp, pa.velocity));
        float speedB = length(fluidSurfDirToLocal(sp, pb.velocity));
        vec3 nA = vec3(0.0), bA = vec3(0.0), nB = vec3(0.0), bB = vec3(0.0);
        vec3 tA = curveDirection(x0, xa, xb, x3, 0.0);
        vec3 tB = curveDirection(x0, xa, xb, x3, 1.0);
        vec3 startNormal, unused;
        ringFrame(tA, startNormal, unused);
        vec3 carried = startNormal;
        vec3 previousTangent = tA;
        for (uint r = 1u; r <= segments; r++)
        {
            vec3 tangent = curveDirection(x0, xa, xb, x3, float(r) / float(segments));
            carried = transportNormal(previousTangent, tangent, carried);
            previousTangent = tangent;
        }
        vec3 endNormal;
        ringFrame(tB, endNormal, unused);
        float roll = atan(dot(endNormal, cross(tB, carried)), dot(endNormal, carried));
        float step = 6.28318530718 / float(sides);
        // An integer ring shift keeps the endpoint geometry identical and minimizes the remaining twist. -xlinka
        roll -= round(roll / step) * step;
        carried = startNormal;
        previousTangent = tA;
        for (uint r = 0u; r <= segments; r++)
        {
            float t = float(r) / float(segments);
            // the end rings come straight from the particles and their chain neighbours, the same inputs the
            // link on the other side of the particle uses, so the two rings meet exactly
            vec3 centre = r == 0u ? xa : (r == segments ? xb : catmull(x0, xa, xb, x3, t));
            vec3 tangent = catmullTangent(x0, xa, xb, x3, t);
            float tl = length(tangent);
            tangent = tl > 1e-9 ? tangent / tl : normalize(xb - xa + vec3(1e-9));
            if (r > 0u)
                carried = transportNormal(previousTangent, tangent, carried);
            previousTangent = tangent;
            float angle = roll * t;
            vec3 n = carried * cos(angle) + cross(tangent, carried) * sin(angle);
            vec3 b = cross(tangent, n);
            if (r == 0u) { nA = n; bA = b; }
            if (r == segments) { nB = n; bB = b; }
            float areaDerivative;
            float radius = neckProfile(rA, neck, rB, t, areaDerivative);
            vec4 tint = mix(tintA, tintB, t);
            vec4 custom = vec4(radius, mix(speedA, speedB, t), mix(pa.wetness, pb.wetness, t), 1.0);
            // The normal follows the taper too, so a neck reflects light along its curved surface. -xlinka
            float slope = polygonScale * areaDerivative / max(2.0 * radius * tl, 1e-9);
            for (uint s = 0u; s < sides; s++)
                ringVertex(r, s, centre, n, b, radius, -tangent * slope, tint, custom);
        }
        uint q = q0;
        for (uint r = 0u; r < segments; r++)
            ringQuads(q, r, r + 1u);

        uint ring = segments + 1u;
        // Round ends follow the same quality as the strand curve. -xlinka
        if (capA)
        {
            vec3 axis = -tA;
            uint previous = 0u;
            for (uint c = 1u; c <= capSegments; c++)
            {
                float angle = 1.57079632679 * float(c) / float(capSegments);
                float radial = c == capSegments ? 0.0 : cos(angle);
                float along = sin(angle);
                vec3 bend = axis * (polygonScale * along / max(radial, 1e-6));
                for (uint s = 0u; s < sides; s++)
                    ringVertex(ring, s, xa + axis * (rA * along), nA, bA, rA * radial, bend, tintA, vec4(rA * radial, speedA, pa.wetness, 1.0));
                ringQuads(q, ring, previous);
                previous = ring++;
            }
        }
        if (capB)
        {
            vec3 axis = tB;
            uint previous = segments;
            for (uint c = 1u; c <= capSegments; c++)
            {
                float angle = 1.57079632679 * float(c) / float(capSegments);
                float radial = c == capSegments ? 0.0 : cos(angle);
                float along = sin(angle);
                vec3 bend = axis * (polygonScale * along / max(radial, 1e-6));
                for (uint s = 0u; s < sides; s++)
                    ringVertex(ring, s, xb + axis * (rB * along), nB, bB, rB * radial, bend, tintB, vec4(rB * radial, speedB, pb.wetness, 1.0));
                ringQuads(q, previous, ring);
                previous = ring++;
            }
        }
    }
}

void tailPass()
{
    uint q = gl_GlobalInvocationID.x;
    uint now = min(counters[FLUID_SC_TUBE_QUADS], sp.tubeQuadCapacity);
    uint last = min(counters[FLUID_SC_TUBE_QUADS_LAST], sp.tubeQuadCapacity);
    if (q >= now && q < last)
    {
        bool w = sp.tubeVertexCapacity > 65536u;
        FLUID_ZERO_QUAD(tib, w, q)
    }
}

void main()
{
    if (pc.pass == 0u)
        buildPass();
    else if (pc.pass == 1u)
        tailPass();
}
