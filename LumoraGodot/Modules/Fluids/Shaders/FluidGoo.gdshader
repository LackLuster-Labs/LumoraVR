// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Glossy goo with coloured transmission through the body. Transmission zero keeps an opaque appearance;
// wet honey and slime show the scene behind through their own colour. The mesh carries COLOR and CUSTOM0
// (thickness in m, speed in m/s, wetness, strandness). -xlinka

shader_type spatial;
// Capture the scene before goo writes depth; its transmitted background needs the opaque scene intact. -xlinka
render_mode cull_back, depth_draw_always, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidGoo.gdshaderinc"
