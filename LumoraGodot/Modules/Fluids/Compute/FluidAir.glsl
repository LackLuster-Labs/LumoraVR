// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Secondary entrained air follows measured liquid and buoyancy; it never debits or creates liquid. -xlinka
#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;

struct AirSample { vec4 point; vec4 normalSpeed; uvec4 identity; };
struct AirBubble { vec4 point; vec4 velocityAge; vec4 medium; };
layout(set = 0, binding = 0, std430) readonly buffer Particles { FluidParticle particles[]; };
layout(set = 0, binding = 1, std430) readonly buffer Live { uint live[]; };
layout(set = 0, binding = 2, std430) readonly buffer Samples { AirSample samples[]; };
layout(set = 0, binding = 3, std430) buffer Readings { vec4 readings[]; };
layout(set = 0, binding = 4, std430) buffer Bubbles { AirBubble bubbles[]; };
layout(set = 0, binding = 5, std430) buffer Counts { uint counts[]; };
layout(set = 0, binding = 6, std430) buffer Free { uint freeSlots[]; };
layout(set = 0, binding = 7, std430) buffer States { uvec4 states[]; };
layout(set = 0, binding = 8, std430) buffer Instances { float instances[]; };
layout(set = 0, binding = 9, std430) readonly buffer Substances { FluidSubstanceRecord substances[]; };
layout(set = 0, binding = 10, std430) readonly buffer Params
{
    vec4 toDraw[3];
    vec4 toWorld[3];
    vec4 fromWorld[3];
    vec4 gravityDt;
    uvec4 sizes;
    uvec4 metadata;
    vec4 tuning;
    uint hidden[8];
} ap;
layout(set = 0, binding = 11, std430) readonly buffer Colliders { FluidColliderRecord colliders[]; };
layout(set = 0, binding = 12, std430) buffer Heads { uint heads[]; };
layout(set = 0, binding = 13, std430) buffer Next { uint nextParticle[]; };
layout(push_constant, std430) uniform Push { uint pass; uint spare0; uint spare1; uint spare2; } pc;

shared vec4 liquid[64];
shared vec4 medium[64];

vec3 transformPoint(vec4 a, vec4 b, vec4 c, vec3 p)
{
    vec4 v = vec4(p, 1.0);
    return vec3(dot(a, v), dot(b, v), dot(c, v));
}

void measure(vec3 point, float radius)
{
    uint lane = gl_LocalInvocationIndex;
    vec4 sum = vec4(0.0);
    vec4 material = vec4(0.0);
    float r2 = radius * radius;
    float cellSize = ap.tuning.x * 4.0;
    ivec3 lo = ivec3(floor((point - vec3(radius)) / cellSize));
    ivec3 hi = ivec3(floor((point + vec3(radius)) / cellSize));
    ivec3 span = hi - lo + ivec3(1);
    uint cells = uint(span.x * span.y * span.z);
    for (uint cell = lane; cell < cells; cell += 64u)
    {
        ivec3 at = lo + ivec3(int(cell) % span.x, (int(cell) / span.x) % span.y, int(cell) / (span.x * span.y));
        uint hash = fluidHashCell(at, 0u, ap.metadata.z - 1u);
        bool duplicate = false;
        for (uint earlier = 0u; earlier < cell; earlier++)
        {
            ivec3 e = lo + ivec3(int(earlier) % span.x, (int(earlier) / span.x) % span.y, int(earlier) / (span.x * span.y));
            duplicate = duplicate || fluidHashCell(e, 0u, ap.metadata.z - 1u) == hash;
        }
        if (duplicate) continue;
        uint link = heads[hash];
        while (link != 0u)
        {
            uint i = link - 1u;
            FluidParticle p = particles[i];
            link = nextParticle[i];
            float d2 = dot(p.position - point, p.position - point);
            if (d2 >= r2) continue;
            float weight = p.volume * pow(1.0 - d2 / r2, 2.0);
            FluidSubstanceRecord s = substances[fluidSubstanceOf(p.material)];
            sum += vec4(p.velocity * weight, weight);
            material += vec4(s.density, s.viscosity, s.spacing, 0.0) * weight;
        }
    }
    liquid[lane] = sum;
    medium[lane] = material;
    barrier();
    for (uint step = 32u; step > 0u; step >>= 1u)
    {
        if (lane < step)
        {
            liquid[lane] += liquid[lane + step];
            medium[lane] += medium[lane + step];
        }
        barrier();
    }
    if (lane == 0u)
    {
        float weight = liquid[0].w;
        liquid[0].xyz /= max(weight, 1e-20);
        medium[0] /= max(weight, 1e-20);
        liquid[0].w = weight / max((32.0 * FLUID_PI / 105.0) * radius * radius * radius, 1e-20);
    }
    barrier();
}

float primitiveSdf(uint kind, vec3 p, vec3 halfExtents, float radius)
{
    if (kind == FLUID_COLLIDER_SPHERE) return length(p) - radius;
    if (kind == FLUID_COLLIDER_CAPSULE) return fluidSdfCapsule(p, halfExtents.y, radius);
    if (kind == FLUID_COLLIDER_CYLINDER) return fluidSdfCylinder(p, halfExtents.y, radius);
    return fluidSdfBox(p, halfExtents);
}

void avoidSolids(inout vec3 p, float radius)
{
    vec3 world = transformPoint(ap.toWorld[0], ap.toWorld[1], ap.toWorld[2], p);
    float worldRadius = radius * ap.tuning.z;
    for (uint i = 0u; i < ap.metadata.y; i++)
    {
        FluidColliderRecord c = colliders[i];
        if (c.kind > FLUID_COLLIDER_CYLINDER || (c.flags & (FLUID_COLLIDER_TRIGGER | FLUID_COLLIDER_DRAIN)) != 0u)
            continue;
        float scale = max(abs(c.scale), 1e-6);
        vec3 local = fluidToLocal(world, c.rotation, c.position, scale);
        float sd = primitiveSdf(c.kind, local, c.halfExtents, c.radius);
        if (sd >= worldRadius / scale) continue;
        float e = max(worldRadius * 0.01 / scale, 1e-5);
        vec2 k = vec2(1.0, -1.0);
        vec3 n = k.xyy * primitiveSdf(c.kind, local + k.xyy * e, c.halfExtents, c.radius)
            + k.yyx * primitiveSdf(c.kind, local + k.yyx * e, c.halfExtents, c.radius)
            + k.yxy * primitiveSdf(c.kind, local + k.yxy * e, c.halfExtents, c.radius)
            + k.xxx * primitiveSdf(c.kind, local + k.xxx * e, c.halfExtents, c.radius);
        n = dot(n, n) > 1e-15 ? normalize(n) : vec3(0.0, 1.0, 0.0);
        world = fluidToWorld(local + n * (worldRadius / scale - sd), c.rotation, c.position, scale);
    }
    p = transformPoint(ap.fromWorld[0], ap.fromWorld[1], ap.fromWorld[2], world);
}

void draw(uint index, AirBubble b)
{
    float radius = b.point.w * clamp(1.0 - b.medium.w / 0.15, 0.0, 1.0);
    vec3 centre = transformPoint(ap.toDraw[0], ap.toDraw[1], ap.toDraw[2], b.point.xyz);
    uint at = index * 16u;
    for (uint copy = 0u; copy <= ap.metadata.w; copy++)
    {
        uint base = at + copy * ap.sizes.z * 16u;
        for (uint row = 0u; row < 3u; row++)
        {
            instances[base + row * 4u + 0u] = ap.toDraw[row].x * radius;
            instances[base + row * 4u + 1u] = ap.toDraw[row].y * radius;
            instances[base + row * 4u + 2u] = ap.toDraw[row].z * radius;
            instances[base + row * 4u + 3u] = centre[row];
        }
        instances[base + 12u] = b.point.w;
        instances[base + 13u] = b.velocityAge.w;
        instances[base + 14u] = b.medium.z;
        instances[base + 15u] = 1.0;
    }
}

// A compact spatial index of this frame's liquid avoids an all-particle scan for each bubble. -xlinka
void gridPass()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= min(live[FLUID_C_LIVE], ap.sizes.x)) return;
    FluidParticle p = particles[i];
    uint phase = fluidPhaseOf(p.phase);
    uint origin = fluidOriginOf(p.material);
    if (phase == FLUID_PHASE_DEAD || phase == FLUID_PHASE_SPRAY || fluidSystemOf(p.material) != ap.sizes.w
        || (ap.hidden[origin >> 5u] & (1u << (origin & 31u))) != 0u) return;
    ivec3 cell = ivec3(floor(p.position / (ap.tuning.x * 4.0)));
    uint hash = fluidHashCell(cell, 0u, ap.metadata.z - 1u);
    nextParticle[i] = atomicExchange(heads[hash], i + 1u);
}

void samplePass()
{
    uint i = gl_WorkGroupID.x;
    if (i >= ap.sizes.y) return;
    measure(samples[i].point.xyz, samples[i].point.w);
    if (gl_LocalInvocationIndex == 0u)
    {
        readings[i * 2u] = liquid[0];
        readings[i * 2u + 1u] = medium[0];
    }
}

void advancePass()
{
    uint i = gl_WorkGroupID.x;
    if (i >= ap.sizes.z) return;
    AirBubble b = bubbles[i];
    bool alive = b.point.w > 0.0;
    if (alive) measure(b.point.xyz, max(ap.tuning.x * 2.0, b.point.w * 2.0));
    if (gl_LocalInvocationIndex != 0u) return;
    if (alive)
    {
        float dt = ap.gravityDt.w;
        float support = liquid[0].w;
        b.medium.z = support;
        if (support < 0.45) b.medium.w += dt;
        else b.medium.w = max(b.medium.w - dt, 0.0);
        if (support >= 0.45)
        {
            b.medium.x = medium[0].y;
            b.medium.y = medium[0].x;
        }
        float gravity = length(ap.gravityDt.xyz);
        vec3 up = gravity > 1e-6 ? -ap.gravityDt.xyz / gravity : vec3(0.0, 1.0, 0.0);
        float viscous = 2.0 * gravity * b.medium.y * b.point.w * b.point.w * ap.tuning.z * ap.tuning.z / (9.0 * max(b.medium.x, 0.001));
        float inertial = sqrt(max(8.0 * gravity * b.point.w / 1.32, 0.0));
        vec3 target = liquid[0].xyz + up * min(viscous, inertial);
        b.velocityAge.xyz = mix(b.velocityAge.xyz, target, 1.0 - exp(-dt * 15.0));
        b.point.xyz += b.velocityAge.xyz * dt;
        avoidSolids(b.point.xyz, b.point.w);
        b.velocityAge.w += dt;
        // Slow dissolution scales with the medium; the free surface releases air immediately. -xlinka
        b.point.w = max(b.point.w - dt * 0.00002 / sqrt(1.0 + b.medium.x), 0.0);
        if (b.medium.w >= 0.15 || b.point.w < 0.0001) b.point.w = 0.0;
    }
    if (b.point.w > 0.0) atomicAdd(counts[0], 1u);
    else freeSlots[atomicAdd(counts[1], 1u)] = i;
    bubbles[i] = b;
    draw(i, b);
}

void spawnPass()
{
    uint i = gl_GlobalInvocationID.x;
    if (i >= ap.sizes.y) return;
    AirSample patchSample = samples[i];
    uint key = patchSample.identity.x;
    uvec4 state = patchSample.identity.z != 0u ? uvec4(0u) : states[key];
    float fill = readings[i * 2u].w;
    bool recent = ap.metadata.x - state.y <= 3u;
    if (!recent) state.z = 0u;
    if (fill < 0.12) state.z = 1u;
    bool entering = state.z == 2u || (recent && state.z == 1u && fill > 0.55 && patchSample.normalSpeed.w > 0.01);
    if (entering && fill > 0.12)
    {
        uint claim = atomicAdd(counts[2], 1u);
        if (claim < counts[1])
        {
            uint slot = freeSlots[claim];
            vec4 material = readings[i * 2u + 1u];
            AirBubble b;
            // The entrained film is limited by the contacted patch size, not by a display budget. -xlinka
            float patchArea = uintBitsToFloat(patchSample.identity.y);
            float volume = patchArea * 0.00008 * ap.tuning.y / pow(ap.tuning.z, 3.0);
            float radius = min(pow(max(3.0 * volume / (4.0 * FLUID_PI), 0.0), 1.0 / 3.0), ap.tuning.x * 0.6);
            b.point = vec4(patchSample.point.xyz + patchSample.normalSpeed.xyz * radius, radius);
            b.velocityAge = vec4(readings[i * 2u].xyz, 0.0);
            b.medium = vec4(material.y, material.x, fill, 0.0);
            avoidSolids(b.point.xyz, b.point.w);
            bubbles[slot] = b;
            draw(slot, b);
            atomicAdd(counts[0], 1u);
            state.z = 0u;
        }
        else
        {
            atomicAdd(counts[3], 1u);
            state.z = 2u;
        }
    }
    else if (fill > 0.55 && state.z != 2u) state.z = 0u;
    state.y = ap.metadata.x;
    states[key] = state;
}

void main()
{
    if (pc.pass == 0u) gridPass();
    else if (pc.pass == 1u) samplePass();
    else if (pc.pass == 2u) advancePass();
    else spawnPass();
}
