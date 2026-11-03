// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Spots times residue bones into the residue MultiMesh, one thread a spot. Spot i is instance i in both
// MultiMeshes: the main one and the wearer's own head one (on the local head layer, so a mirror shows your own
// face's goo and your eyes do not). A spot draws in one of the two and is a zero-size instance in the other, which
// keeps the mapping fixed and needs no count back from the GPU. The instance is the bone matrix times the spot's
// own frame, the spot mesh's pole (its y) on the spot normal:
//   a coat   a flattened ellipsoid of the spot's volume;
//   a film   what a liquid leaves (meta bit 25, or until the people side sets it, a spot of at most a soaked
//            patch in a clear, light colour): spread thin over a patch, a lens 0.8 mm high just off the skin,
//            stretched downhill along the skin by how steep it is there, so water reads as a sheen running down.
// INSTANCE_CUSTOM = (wetness, cure, film, volume in mL). FluidResiduePass.SpotInstance is the same on the CPU.
//
// Push constants: pass = flags (bit 0 the main buffer holds two copies, bit 1 the head buffer does), substep =
// instances allocated, arg = spots. A MultiMesh only doubles its buffer when a CPU setter runs while a view keeps
// motion vectors; the hook never calls one, but if the buffer ever comes back doubled both copies are written.
//
// Spot packing (FluidSpotRecord.normalShape): octahedral normal 12 + 12 bits, flatten 8 bits (0..1, the short
// axis over the long). meta: wetness 8, cure 8, bit 24 = on the wearer's head, bit 25 = a film. -xlinka

// Godot's import hashes this file alone and never its includes, so an include change recompiles nothing until
// this stamp changes too. Include stamp 1.

#[compute]
#version 450

#include "Include/FluidLayout.glsli"
#include "Include/FluidKernels.glsli"

layout(local_size_x = FLUID_GROUP_SIZE, local_size_y = 1, local_size_z = 1) in;

layout(push_constant, std430) uniform PushBlock { FluidPushConstants pc; };
layout(set = 0, binding = 0, std430) readonly buffer SpotBuffer { FluidSpotRecord spots[]; };
layout(set = 0, binding = 1, std430) readonly buffer BoneBuffer { FluidResidueBone bones[]; };
layout(set = 0, binding = 2, std430) buffer MainBuffer { FluidResidueInstance mainInstances[]; };
layout(set = 0, binding = 3, std430) buffer HeadBuffer { FluidResidueInstance headInstances[]; };

#define FLUID_SPOT_HEAD 0x01000000u
#define FLUID_SPOT_FILM 0x02000000u
// mL: a soaked patch's film (FluidResidueBaker.WetFilmMl), with room for rounding
#define FLUID_FILM_MAX_ML 0.105
// m: how thin a film spreads, its patch's radius limits, the lens's half height and its lift off the skin
#define FLUID_FILM_THICK 0.00015
#define FLUID_FILM_MIN_R 0.008
#define FLUID_FILM_MAX_R 0.022
#define FLUID_FILM_LENS 0.0008
#define FLUID_FILM_LIFT 0.0006

// A spot of at most a soaked patch in a light, nearly grey colour: the film a clear liquid leaves.
bool spotIsFilm(FluidSpotRecord spot, vec4 tint)
{
    if ((spot.meta & FLUID_SPOT_FILM) != 0u)
        return true;
    float lo = min(tint.r, min(tint.g, tint.b));
    float hi = max(tint.r, max(tint.g, tint.b));
    return spot.volumeMl <= FLUID_FILM_MAX_ML && lo >= 0.75 && hi - lo <= 0.2;
}

vec3 spotNormal(uint packed)
{
    vec2 e = vec2(float(packed & 4095u), float((packed >> 12) & 4095u)) / 4095.0;
    return fluidOctDecode(e);
}

FluidResidueInstance zeroInstance()
{
    FluidResidueInstance z;
    z.row0 = vec4(0.0);
    z.row1 = vec4(0.0);
    z.row2 = vec4(0.0);
    z.color = vec4(0.0);
    z.custom = vec4(0.0);
    return z;
}

void main()
{
    uint i = gl_GlobalInvocationID.x;
    uint capacity = pc.substep;
    uint count = min(pc.arg, capacity);
    if (i >= count)
        return;
    FluidSpotRecord spot = spots[i];
    FluidResidueInstance inst = zeroInstance();
    if (spot.volumeMl > 0.0)
    {
        FluidResidueBone bone = bones[spot.bone];
        mat3 rot = transpose(mat3(bone.row0.xyz, bone.row1.xyz, bone.row2.xyz));
        vec3 origin = vec3(bone.row0.w, bone.row1.w, bone.row2.w);
        vec4 tint = fluidUnpackTint(spot.tint);
        vec3 n = spotNormal(spot.normalShape);
        vec3 t1 = abs(n.y) < 0.99 ? normalize(cross(vec3(0.0, 1.0, 0.0), n)) : normalize(cross(vec3(1.0, 0.0, 0.0), n));
        vec3 local = spot.local;
        bool film = spotIsFilm(spot, tint);
        // the spot's frame in bone space, columns: across, along the normal (the mesh's pole), across again;
        // (t1, n, -t2) keeps the mesh's winding
        mat3 s;
        if (film)
        {
            // downhill on the skin: the world's down in bone space, along the surface
            vec3 down = transpose(rot) * vec3(0.0, -1.0, 0.0);
            down = down / max(length(down), 1e-6);
            vec3 slope = down - n * dot(down, n);
            float steep = length(slope);
            if (steep > 0.05)
                t1 = slope / steep;
            vec3 t2 = cross(n, t1);
            float a = clamp(sqrt(spot.volumeMl * 1e-6 / (FLUID_PI * FLUID_FILM_THICK)), FLUID_FILM_MIN_R, FLUID_FILM_MAX_R);
            float run = 1.0 + 1.2 * smoothstep(0.15, 0.7, steep);
            s = mat3(t1 * (a * run), n * FLUID_FILM_LENS, -t2 * a);
            local += t1 * (a * (run - 1.0) * 0.8) + n * FLUID_FILM_LIFT;
        }
        else
        {
            float flatten = max(float(spot.normalShape >> 24) / 255.0, 0.05);
            // V = 4/3 pi a a (a flatten), in m3 from mL
            float a = pow(spot.volumeMl * 1e-6 * 0.2387324 / flatten, 1.0 / 3.0);
            vec3 t2 = cross(n, t1);
            s = mat3(t1 * a, n * (a * flatten), -t2 * a);
        }
        mat3 m = rot * s;
        vec3 o = rot * local + origin;
        inst.row0 = vec4(m[0].x, m[1].x, m[2].x, o.x);
        inst.row1 = vec4(m[0].y, m[1].y, m[2].y, o.y);
        inst.row2 = vec4(m[0].z, m[1].z, m[2].z, o.z);
        inst.color = tint;
        inst.custom = vec4(float(spot.meta & 255u) / 255.0, float((spot.meta >> 8) & 255u) / 255.0, film ? 1.0 : 0.0, spot.volumeMl);
    }
    bool head = (spot.meta & FLUID_SPOT_HEAD) != 0u;
    FluidResidueInstance none = zeroInstance();
    FluidResidueInstance forMain = head ? none : inst;
    FluidResidueInstance forHead = head ? inst : none;
    mainInstances[i] = forMain;
    headInstances[i] = forHead;
    if ((pc.pass & 1u) != 0u)
        mainInstances[capacity + i] = forMain;
    if ((pc.pass & 2u) != 0u)
        headInstances[capacity + i] = forHead;
}
