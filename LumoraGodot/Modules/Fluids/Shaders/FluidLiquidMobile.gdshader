// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Liquid on the Mobile renderer: no screen or depth reads, which would break the tile renderer's single pass.
// Premultiplied alpha (FluidLiquidMobile.gdshaderinc): the reflection and the highlights stay at full strength
// while the scene shows through, dimmed by how much liquid the view crosses. Writes no depth, so whatever else is
// transparent still draws through it. -xlinka

shader_type spatial;
render_mode cull_back, depth_draw_never, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidLiquidMobile.gdshaderinc"
