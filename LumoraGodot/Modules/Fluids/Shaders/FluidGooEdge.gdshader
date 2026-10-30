// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Strands and spray keep the body's colour, absorption and transmission. Their true radius controls the
// optical path, while per-view widening only preserves subpixel coverage. CUSTOM0 is (radius, speed, wetness,
// kind): kind 1 is a tube and 2 + corner is a spray billboard. Spray is shaded as a rounded drop. The same
// near-eye cut is enabled on the eyes-only material copy. -xlinka

shader_type spatial;
// Capture the scene before goo writes depth; its transmitted background needs the opaque scene intact. -xlinka
render_mode cull_back, depth_draw_always, blend_premul_alpha, skip_vertex_transform, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidLook.gdshaderinc"
#include "res://Modules/Fluids/Shaders/FluidNearEye.gdshaderinc"

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;

uniform float tube_sides = 6.0;
uniform float min_pixels = 0.75;

varying vec4 fluid_custom;
varying float fluid_coverage;
varying vec2 fluid_drop;

void vertex()
{
	fluid_custom = CUSTOM0;
	fluid_drop = vec2(0.0);
	vec3 v = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 n = normalize((MODELVIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	float scale = length(MODELVIEW_MATRIX[0].xyz);
	bool ortho = PROJECTION_MATRIX[3][3] != 0.0;
	// a pixel's size in view units at this depth
	float pixel = 2.0 * (ortho ? 1.0 : max(-v.z, 1e-4)) / (PROJECTION_MATRIX[1][1] * VIEWPORT_SIZE.y);
	float coverage = 1.0;
	if (CUSTOM0.w >= 1.5)
	{
		float corner = CUSTOM0.w - 2.0;
		vec2 c = vec2(corner < 1.5 ? -1.0 : 1.0, (corner > 0.5 && corner < 2.5) ? 1.0 : -1.0);
		float r = CUSTOM0.x * scale;
		float drawn = max(r, min_pixels * pixel);
		coverage = (r * r) / (drawn * drawn);
		fluid_drop = c;
		v.xy += c * drawn;
		n = vec3(0.0, 0.0, 1.0);
	}
	else
	{
		float k = sqrt(6.2831853 / (tube_sides * sin(6.2831853 / tube_sides)));
		float r = CUSTOM0.x * scale * k;
		// A collapsed cap tip stays at its physical endpoint. -xlinka
		float drawn = r > 0.0 ? max(r, min_pixels * pixel) : 0.0;
		v += n * (drawn - r);
		coverage = r > 0.0 ? r / drawn : 1.0;
	}
	fluid_coverage = clamp(coverage, 0.0, 1.0);
	VERTEX = v;
	NORMAL = n;
}

void fragment()
{
	if (near_eye_radius > 0.0 && fluid_near_eye_cut(VERTEX, EYE_OFFSET, FRAGCOORD.xy))
	{
		discard;
	}
	float speed = fluid_custom.y;
	float wetness = fluid_custom.z;
	float coverage = fluid_coverage;
	vec3 n = normalize(NORMAL);
	float radial_path;
	if (fluid_custom.w >= 1.5)
	{
		float radial = dot(fluid_drop, fluid_drop);
		if (radial >= 1.0)
			discard;
		float front = sqrt(max(1.0 - radial, 0.0));
		n = vec3(fluid_drop, front);
		radial_path = front;
		coverage *= 1.0 - smoothstep(1.0 - max(fwidth(radial), 0.001), 1.0, radial);
	}
	else
	{
		// A round strand gets thinner towards its silhouette, unlike a sheet. -xlinka
		radial_path = clamp(dot(n, VIEW), 0.0, 1.0);
	}
	float facing = clamp(dot(n, VIEW), 0.0, 1.0);
	float thickness = min(max(fluid_custom.x, 0.0) * 2.0 * radial_path, 0.3);
	vec3 albedo;
	vec3 emitted;
	float cover;
	vec3 behind = textureLod(screen_tex, SCREEN_UV, 0.0).rgb;
	fluid_goo_surface(COLOR.rgb, thickness, speed, wetness, facing, behind, albedo, emitted, cover);
	ALBEDO = albedo * coverage;
	ROUGHNESS = fluid_filtered_roughness(n, fluid_roughness(wetness));
	METALLIC = 0.0;
	SPECULAR = 0.5 * coverage;
	CLEARCOAT = clearcoat * clamp(wetness, 0.0, 1.0) * coverage;
	CLEARCOAT_ROUGHNESS = ROUGHNESS;
	NORMAL = n;
	BACKLIGHT = fluid_backlight(COLOR.rgb, thickness) * coverage;
	EMISSION = emitted * coverage;
	ALPHA = cover * coverage;
	if (liquid > 0.5)
	{
		vec3 colour = clamp(tint.rgb * COLOR.rgb, vec3(0.02), vec3(1.0));
		vec3 through = fluid_transmittance(COLOR.rgb, thickness);
		float fresnel = 0.02 + 0.98 * pow(1.0 - facing, 5.0);
		float whitecap = foam * smoothstep(1.5, 4.0, speed);
		cover = clamp(max(thickness / 0.03, max(whitecap, 1.0 - through.b)), 0.25, 0.75);
		ALBEDO = mix(colour * (vec3(1.0) - through) * 0.2, vec3(0.9), whitecap) * cover * coverage;
		ROUGHNESS = clamp(1.0 - gloss, 0.02, 1.0);
		CLEARCOAT = 0.0;
		BACKLIGHT = vec3(0.0);
		EMISSION = (behind * through * (1.0 - fresnel) * (1.0 - whitecap) * cover + emission.rgb) * coverage;
		ALPHA = cover * coverage;
	}
}
