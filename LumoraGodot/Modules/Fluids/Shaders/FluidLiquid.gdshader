// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Liquid on desktop: the scene behind, read from the screen along the bent ray and dimmed by Beer-Lambert over the
// liquid the view crosses, with the sky and the lights reflected on top (FluidLiquid.gdshaderinc). Reading the
// screen puts it in the transparent pass. It writes no depth: what else is transparent (a grid's lines under a
// puddle) still draws through it, and being premultiplied, a thin film or stream crossing another blends with it
// in either order instead of hiding it. debug_view 1 shows the thickness instead (5 per metre), 2 the gap to the
// scene and 3 the liquid's own depth, for tuning. -xlinka

shader_type spatial;
render_mode cull_back, depth_draw_never, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidLiquid.gdshaderinc"
