// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Substep, kernel 2 of 4: the volume-weighted fill and lambda of the one-sided density constraint, gathered from the
// neighbour list at the predicted positions (asleep neighbours sit still at their A position). Strand particles
// count their neighbours, which the frame end's classifier reads, but take no lambda: a thread is held by its
// bonds, not by pressure. -xlinka

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
FLUID_DECLARE_LISTS
FLUID_DECLARE_SLOTS
FLUID_DECLARE_AUX
FLUID_DECLARE_SUBSTANCES
FLUID_DECLARE_COUNTERS
FLUID_DECLARE_PUSH

shared vec4 sA[FLUID_GROUP_SIZE];
shared vec4 sB[FLUID_GROUP_SIZE];

// lambda's relaxation, scaled by the lattice's own sum of squared gradients (about 0.37 / d^2)
#define LAMBDA_EPSILON 0.01

void main()
{
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
    vec3 x = P.position;
    uint count = inRange ? floatBitsToUint(slots.v[si * FLUID_SLOT_STRIDE].z) : 0u;

    vec3 gradI = vec3(0.0);
    float fill = 0.0;
    float sumGrad2 = 0.0;
    float neighbours = 0.0;
    for (uint e = lane; e < count; e += FLUID_LANES)
    {
        uint j = lists.v[si * FLUID_MAX_NEIGHBOURS + e];
        vec3 xj = j < awake ? pb.p[j].position : pa.p[j].position;
        vec3 r = x - xj;
        float r2 = dot(r, r);
        if (r2 >= h2)
            continue;
        float len = sqrt(r2);
        float vj = pa.p[j].volume;
        fill += vj * fluidPoly6(r2, h2, poly6);
        vec3 grad = (vj / restFill) * fluidSpikyGrad(r, len, h, spiky);
        sumGrad2 += dot(grad, grad);
        gradI += grad;
        neighbours += 1.0;
    }
    sA[lid] = vec4(gradI, fill);
    sB[lid] = vec4(sumGrad2, neighbours, 0.0, 0.0);
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

    vec4 a = sA[lid];
    float total = a.w + P.volume * fluidPoly6(0.0, h2, poly6);
    float c = fluidDensityConstraint(total, restFill);
    float epsilon = LAMBDA_EPSILON / (sub.spacing * sub.spacing);
    float lambda = fluidPhaseOf(P.phase) == FLUID_PHASE_STRAND ? 0.0 : fluidLambda(c, sB[lid].x, a.xyz, epsilon);
    uint b = i * FLUID_SLOT_STRIDE;
    slots.v[b].x = lambda;
    slots.v[b].y = total;
    aux.v[idA.v[i] * FLUID_AUX_STRIDE].w = sB[lid].y;
}
