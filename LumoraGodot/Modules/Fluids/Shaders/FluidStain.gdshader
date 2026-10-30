// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Stains on world surfaces as quads laid on the surface, for the Mobile renderer (Forward+ uses decals). The
// instance colour is the stain's tint, INSTANCE_CUSTOM.x how much is left; the edge is soft and a little uneven
// so a grid of stain cells does not read as squares. -xlinka

shader_type spatial;
render_mode cull_back, depth_draw_never, blend_mix, diffuse_burley;

uniform float roughness : hint_range(0.0, 1.0) = 0.6;

varying float stain_amount;
varying vec2 stain_seed;

void vertex()
{
	stain_amount = clamp(INSTANCE_CUSTOM.x, 0.0, 1.0);
	stain_seed = INSTANCE_CUSTOM.yz;
}

void fragment()
{
	vec2 d = UV * 2.0 - 1.0;
	float wobble = 0.08 * sin(atan(d.y, d.x) * 5.0 + stain_seed.x * 6.2831853) + 0.05 * sin(atan(d.y, d.x) * 9.0 + stain_seed.y * 6.2831853);
	float edge = 1.0 - smoothstep(0.55 + wobble, 0.95 + wobble, length(d));
	ALBEDO = COLOR.rgb;
	ROUGHNESS = roughness;
	ALPHA = edge * stain_amount * COLOR.a;
}
