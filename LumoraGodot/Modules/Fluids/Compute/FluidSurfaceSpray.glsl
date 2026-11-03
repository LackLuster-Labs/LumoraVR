// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Spray quads (water, High and up). pc.pass picks the step:
//   0 build  per spray particle: four vertices on its centre and one quad, at fixed slots (particle i owns
//            vertices 4i..4i+3 and quad i). CUSTOM0 carries the radius and the corner (w = 2 + corner); the edge
//            material turns the quad to face each view in its vertex shader, so both eyes and every mirror get a
//            billboard of their own. A dead particle writes an all-zero quad.
//   1 tail   per quad: zero what last frame drew past this frame's count.
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
FLUID_SURF_DECLARE_COUNTERS
FLUID_SURF_DECLARE_SPRAY
FLUID_SURF_DECLARE_SPRAY_MESH

void buildPass()
{
    uint i = gl_GlobalInvocationID.x;
    uint count = (sp.flags & FLUID_SURF_SPRAY) != 0u ? min(sp.sprayCount, min(sp.sprayQuadCapacity, sp.sprayVertexCapacity / 4u)) : 0u;
    if (i == 0u)
        counters[FLUID_SC_SPRAY_QUADS] = count;
    if (i >= count)
        return;
    bool wide = sp.sprayVertexCapacity > 65536u;
    FluidParticle p = sprayParticles[i];
    if (fluidPhaseOf(p.phase) == FLUID_PHASE_DEAD || fluidSurfOriginHidden(sp, fluidOriginOf(p.material)))
    {
        FLUID_ZERO_QUAD(sib, wide, i)
        return;
    }
    vec3 centre = fluidSurfToLocal(sp, p.position);
    float radius = p.volume > 0.0 ? min(pow(p.volume * 0.2387324, 1.0 / 3.0), sp.sprayRadius * 4.0) : sp.sprayRadius;
    float speed = length(fluidSurfDirToLocal(sp, p.velocity));
    vec4 tint = fluidUnpackTint(p.tint);
    for (uint c = 0u; c < 4u; c++)
    {
        uint v = i * 4u + c;
        FLUID_WRITE_VERTEX(svb, sab, sp.sprayVertexCapacity, sp.sprayNormalWords, v, centre, vec3(0.0, 1.0, 0.0), tint, vec4(radius, speed, p.wetness, 2.0 + float(c)))
    }
    FLUID_WRITE_QUAD(sib, wide, i, i * 4u, i * 4u + 1u, i * 4u + 2u, i * 4u + 3u)
}

void tailPass()
{
    uint q = gl_GlobalInvocationID.x;
    uint now = min(counters[FLUID_SC_SPRAY_QUADS], sp.sprayQuadCapacity);
    uint last = min(counters[FLUID_SC_SPRAY_QUADS_LAST], sp.sprayQuadCapacity);
    if (q >= now && q < last)
    {
        bool wide = sp.sprayVertexCapacity > 65536u;
        FLUID_ZERO_QUAD(sib, wide, q)
    }
}

void main()
{
    if (pc.pass == 0u)
        buildPass();
    else
        tailPass();
}
