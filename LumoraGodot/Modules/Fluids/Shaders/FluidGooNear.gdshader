// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// FluidGoo with the near-eye cut (FluidNearEye.gdshaderinc), for the renderer's eyes-only twin while the head
// is close to the fluid. -xlinka

shader_type spatial;
// Capture the scene before goo writes depth; its transmitted background needs the opaque scene intact. -xlinka
render_mode cull_back, depth_draw_always, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#define FLUID_NEAR_EYE
#include "res://Modules/Fluids/Shaders/FluidGoo.gdshaderinc"
