// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// FluidLiquidMobile with the near-eye cut (FluidNearEye.gdshaderinc), for the renderer's eyes-only twin while the
// head is close to the fluid. -xlinka

shader_type spatial;
render_mode cull_back, depth_draw_never, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#define FLUID_NEAR_EYE
#include "res://Modules/Fluids/Shaders/FluidLiquidMobile.gdshaderinc"
