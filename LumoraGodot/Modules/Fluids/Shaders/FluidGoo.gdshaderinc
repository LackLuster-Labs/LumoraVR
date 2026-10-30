// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The goo body, shared by FluidGoo.gdshader and its Near variant (FluidGooNear.gdshader), which
// defines FLUID_NEAR_EYE first. -xlinka

#include "res://Modules/Fluids/Shaders/FluidLook.gdshaderinc"
#ifdef FLUID_NEAR_EYE
#include "res://Modules/Fluids/Shaders/FluidNearEye.gdshaderinc"
#endif

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;

varying vec4 fluid_custom;

void vertex()
{
	fluid_custom = CUSTOM0;
	// Decode the deep marker before interpolation so it cannot turn into metre-thick triangles. -xlinka
	fluid_custom.x = CUSTOM0.x >= 0.99 ? 0.06 : max(CUSTOM0.x, 0.0);
}

void fragment()
{
#ifdef FLUID_NEAR_EYE
	if (fluid_near_eye_cut(VERTEX, EYE_OFFSET, FRAGCOORD.xy))
	{
		discard;
	}
#endif
	vec3 n = normalize(NORMAL);
	float facing = clamp(dot(n, VIEW), 0.0, 1.0);
	float thickness = clamp(fluid_custom.x / max(facing, 0.25), 0.0, 0.3);
	float speed = fluid_custom.y;
	float wetness = fluid_custom.z;
	vec3 albedo;
	vec3 emitted;
	float cover;
	fluid_goo_surface(COLOR.rgb, thickness, speed, wetness, facing, textureLod(screen_tex, SCREEN_UV, 0.0).rgb,
		albedo, emitted, cover);
	ALBEDO = albedo;
	ROUGHNESS = fluid_filtered_roughness(n, fluid_roughness(wetness));
	METALLIC = 0.0;
	SPECULAR = 0.5;
	CLEARCOAT = clearcoat * clamp(wetness, 0.0, 1.0);
	CLEARCOAT_ROUGHNESS = ROUGHNESS;
	NORMAL = n;
	BACKLIGHT = fluid_backlight(COLOR.rgb, thickness);
	EMISSION = emitted;
	ALPHA = cover;
}
