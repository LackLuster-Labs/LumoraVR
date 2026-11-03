// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Classify branches, resolve their planes, measure support, reserve owned links, then finalize volume.
// Every stage has a dispatch barrier. Neighbour reads never overlap writes to their source fields. -xlinka
// Include stamp 1.
#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"
#include "Include/FluidSurface.glsli"
layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;
FLUID_SURF_DECLARE_PUSH
FLUID_SURF_DECLARE_PARAMS
FLUID_SURF_DECLARE_PARTICLES
FLUID_SURF_DECLARE_BONDS
FLUID_SURF_DECLARE_LIVE
#include "Include/FluidSurfaceStrand.glsli"
#include "Include/FluidSurfaceCoat.glsli"
#include "Include/FluidSurfaceShape.glsli"

uint backlink(uint slot, uint id)
{
    uint base = fluidSurfBondBase(slot);
    for (uint k = 0u; k < sp.bondSlots; k++)
        if (bonds[base + k].partner == id) return k;
    return FLUID_NONE;
}

void reserveEndpoint(uint slot, float neckVolume, float linkLength)
{
    float fraction = min(neckVolume / max(particles[slot].volume, 1e-30), 1.0);
    atomicAdd(surfaceShapes[slot].account.x, uint(round(fraction * FLUID_SURF_VOLUME_SCALE)));
    atomicAdd(surfaceShapes[slot].account.y, 1u);
    atomicAdd(surfaceShapes[slot].account.z, uint(round(linkLength * FLUID_SURF_LENGTH_SCALE)));
}

bool resolvedLink(uint a, uint b)
{
    if ((surfaceShapes[a].state.x & surfaceShapes[b].state.x & FLUID_SURF_SHAPE_FILM) == 0u) return false;
    FluidSurfaceShape shapeA = fluidSurfSupportedShape(a);
    FluidSurfaceShape shapeB = fluidSurfSupportedShape(b);
    // Final volume allocation can shrink the normal, but not below its sampling radius or change its peak. -xlinka
    float heightA = min(shapeA.radius.z, 2.0 * sp.cellSize);
    float heightB = min(shapeB.radius.z, 2.0 * sp.cellSize);
    shapeA.weight *= heightA / shapeA.radius.z;
    shapeB.weight *= heightB / shapeB.radius.z;
    shapeA.radius.z = heightA;
    shapeB.radius.z = heightB;
    vec2 before = vec2(fluidSurfShapeWeight(shapeA, shapeA.centre), fluidSurfShapeWeight(shapeB, shapeA.centre));
    for (uint step = 1u; step <= 4u; step++)
    {
        vec3 position = mix(shapeA.centre, shapeB.centre, float(step) * 0.25);
        vec2 after = vec2(fluidSurfShapeWeight(shapeA, position), fluidSurfShapeWeight(shapeB, position));
        // Each ellipsoid's minimum along a line lies at an interval endpoint; their sum is a lower bound. -xlinka
        vec2 lower = min(before, after);
        if (lower.x + lower.y < sp.iso * 1.1) return false;
        before = after;
    }
    return true;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= FLUID_SURF_LIVE) return;
    FluidParticle p = particles[i];
    if (pc.pass == 0u)
    {
        FluidSurfaceShapeRecord record;
        record.centreWeight = vec4(0.0);
        record.axis0Radius = vec4(1.0, 0.0, 0.0, 1.0);
        record.axis1Radius = vec4(0.0, 1.0, 0.0, 1.0);
        record.axis2Radius = vec4(0.0, 0.0, 1.0, 1.0);
        record.trailCompression = vec4(0.0, 0.0, 0.0, 1.0);
        record.account = uvec4(0u);
        record.allocation = vec4(0.0);
        record.state = uvec4(0u);
        record.seedNormalWidth = vec4(0.0, 0.0, 1.0, 0.0);
        record.seedTangentLength = vec4(1.0, 0.0, 0.0, 0.0);
        if (fluidSurfSplats(i, p))
        {
            FluidSurfaceShape shape = fluidSurfInitialShape(i, p);
            record.centreWeight = vec4(shape.centre, shape.weight);
            record.axis0Radius = vec4(shape.axes[0], shape.radius.x);
            record.axis1Radius = vec4(shape.axes[1], shape.radius.y);
            record.axis2Radius = vec4(shape.axes[2], shape.radius.z);
            record.trailCompression = vec4(shape.trail, shape.compression);
            record.state.x = FLUID_SURF_SHAPE_VALID
                | (fluidPhaseOf(p.phase) != FLUID_PHASE_STRAND ? FLUID_SURF_SHAPE_FIELD : 0u);
            mat3 axes;
            vec2 reach;
            float ratio;
            uint degree;
            bool candidate = fluidSurfPlaneSeed(i, p, axes, reach, ratio, degree);
            record.seedNormalWidth = vec4(axes[2], reach.y);
            record.seedTangentLength = vec4(axes[0], reach.x);
            record.state.w = (degree << 8) | (candidate ? 1u : 0u) | (candidate && ratio < 0.25 ? 2u : 0u);
        }
        surfaceShapes[i] = record;
    }
    else if (pc.pass == 1u)
    {
        if ((surfaceShapes[i].state.x & FLUID_SURF_SHAPE_VALID) == 0u) return;
        mat3 axes;
        vec2 reach;
        if (!fluidSurfResolvedPlane(i, axes, reach)) return;
        float height = max(8.0 * p.volume / max(reach.x * reach.y, 1e-20), 1e-9);
        float sampleHeight = max(height, 2.0 * sp.cellSize);
        surfaceShapes[i].axis0Radius = vec4(axes[0], reach.x);
        surfaceShapes[i].axis1Radius = vec4(axes[1], reach.y);
        surfaceShapes[i].axis2Radius = vec4(axes[2], sampleHeight);
        surfaceShapes[i].trailCompression = vec4(0.0, 0.0, 0.0, height / sampleHeight);
        surfaceShapes[i].centreWeight.w = p.volume * sampleHeight / height;
        surfaceShapes[i].state.x |= FLUID_SURF_SHAPE_FIELD | FLUID_SURF_SHAPE_FILM;
    }
    else if (pc.pass == 2u)
    {
        float factor = 1.0;
        if ((surfaceShapes[i].state.x & FLUID_SURF_SHAPE_FILM) != 0u)
        {
            vec3 centre = surfaceShapes[i].centreWeight.xyz;
            float density = fluidSurfShapeWeight(fluidSurfShape(i, p), centre);
            uint base = fluidSurfBondBase(i);
            for (uint k = 0u; k < sp.bondSlots; k++)
            {
                uint other;
                if (!fluidSurfNeighbour(p, bonds[base + k].partner, other)
                    || (surfaceShapes[other].state.x & FLUID_SURF_SHAPE_FIELD) == 0u) continue;
                density += fluidSurfShapeWeight(fluidSurfShape(other, particles[other]), centre);
            }
            // Retain the integral and in-plane area while resolving the normal of a sparse sheet. -xlinka
            factor = clamp(density / max(2.0 * sp.iso, 1e-6), 1e-4, 1.0);
        }
        surfaceShapes[i].state.z = floatBitsToUint(factor);
    }
    else if (pc.pass == 3u)
    {
        if ((surfaceShapes[i].state.x & FLUID_SURF_SHAPE_VALID) == 0u) return;
        uint base = fluidSurfBondBase(i);
        uint id = fluidSurfKey(i);
        for (uint k = 0u; k < sp.bondSlots; k++)
        {
            FluidBond bond = bonds[base + k];
            uint j;
            if (!fluidSurfNeighbour(p, bond.partner, j)
                || (surfaceShapes[j].state.x & FLUID_SURF_SHAPE_VALID) == 0u) continue;
            uint back = backlink(j, id);
            if (bond.partner < id && back != FLUID_NONE) continue;
            atomicAdd(surfaceShapes[i].account.w, 1u);
            atomicAdd(surfaceShapes[j].account.w, 1u);
            bool chain = fluidSurfIsChain(bond) || (back != FLUID_NONE && fluidSurfIsChain(bonds[fluidSurfBondBase(j) + back]));
            if (!chain || (sp.flags & FLUID_SURF_STRANDS) == 0u || resolvedLink(i, j))
            {
                atomicOr(surfaceShapes[i].state.y, 1u << k);
                if (back != FLUID_NONE) atomicOr(surfaceShapes[j].state.y, 1u << back);
                continue;
            }
            float length = distance(p.position, particles[j].position);
            float radius = min(sp.maxRadius, sqrt(min(p.volume, particles[j].volume) / max(FLUID_PI * length * 0.5, 1e-20)));
            if (bond.radius > 0.0) radius = min(radius, bond.radius);
            if (back != FLUID_NONE)
            {
                float otherRadius = bonds[fluidSurfBondBase(j) + back].radius;
                if (otherRadius > 0.0) radius = min(radius, otherRadius);
            }
            float halfVolume = FLUID_PI * radius * radius * length * 0.5;
            reserveEndpoint(i, halfVolume, length);
            reserveEndpoint(j, halfVolume, length);
        }
    }
    else
    {
        FluidSurfaceShapeRecord record = surfaceShapes[i];
        if ((record.state.x & FLUID_SURF_SHAPE_VALID) == 0u) return;
        if (record.account.y == 0u) record.state.x |= FLUID_SURF_SHAPE_FIELD;
        bool fieldOwner = (record.state.x & FLUID_SURF_SHAPE_FIELD) != 0u;
        float reserved = p.volume * min(float(record.account.x) / FLUID_SURF_VOLUME_SCALE, 1.0);
        float tubeShare = float(record.account.y) / max(float(record.account.w), 1.0);
        float tubeVolume = fieldOwner ? min(reserved, p.volume * tubeShare) : p.volume;
        float fieldVolume = max(p.volume - tubeVolume, 0.0);
        record.allocation = vec4(tubeVolume, fieldVolume, reserved, 0.0);
        if (fieldVolume <= 0.0) record.state.x &= ~(FLUID_SURF_SHAPE_FIELD | FLUID_SURF_SHAPE_FILM);
        if ((record.state.x & FLUID_SURF_SHAPE_FILM) != 0u)
        {
            float factor = uintBitsToFloat(record.state.z);
            float height = max(8.0 * fieldVolume * factor / max(record.axis0Radius.w * record.axis1Radius.w, 1e-20), 1e-9);
            float sampleHeight = max(height, 2.0 * sp.cellSize);
            record.axis2Radius.w = sampleHeight;
            record.trailCompression.w = height / sampleHeight;
            record.centreWeight.w = fieldVolume / record.trailCompression.w;
        }
        else record.centreWeight.w *= fieldVolume / max(p.volume, 1e-30);
        surfaceShapes[i] = record;
    }
}
