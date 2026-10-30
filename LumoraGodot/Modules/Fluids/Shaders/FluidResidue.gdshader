// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Residue spots on people, one MultiMesh instance each (FluidResidue.glsl lays them out). The instance colour is the
// spot's tint; INSTANCE_CUSTOM = (wetness, cure, film, volume in mL).
//   A coat (goo) is a flattened ellipsoid in the goo look, solid: a fresh one glossy, a dried or cured one matte.
//   A film (what water leaves) is a thin lens just off the skin, run downhill: written premultiplied, it darkens
//   the skin a little and lays the sky's reflection and the highlights over it at full strength, with streaks
//   along the way it runs and a soft edge, so a wet face reads as a sheen running down and not as a sticker. It
//   fades as it dries.
// Premultiplied blending puts every spot in the transparent pass; a coat writes ALPHA 1, so it covers as before.
// -xlinka

shader_type spatial;
render_mode cull_back, depth_prepass_alpha, blend_premul_alpha, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidLook.gdshaderinc"

// how much a soaked film darkens what is under it
uniform float film_cover : hint_range(0.0, 1.0) = 0.26;

varying vec4 spot_state;
varying vec3 spot_local;
// the instance's axes in view space: the pole (the skin normal), downhill, across
varying vec3 spot_up;
varying vec3 spot_run;
varying vec3 spot_across;

void vertex()
{
	spot_state = INSTANCE_CUSTOM;
	spot_local = VERTEX;
	spot_up = (MODELVIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz;
	spot_run = (MODELVIEW_MATRIX * vec4(1.0, 0.0, 0.0, 0.0)).xyz;
	spot_across = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 1.0, 0.0)).xyz;
}

void fragment()
{
	float wetness = clamp(spot_state.x, 0.0, 1.0);
	float cure = spot_state.y;
	if (spot_state.z > 0.5)
	{
		// the mesh's pole (y) is the normal, x runs downhill, z across
		float r = length(spot_local.xz);
		float edge = 1.0 - smoothstep(0.45, 1.0, r);
		float soak = clamp(spot_state.w / 0.1, 0.35, 1.0);
		float cover = film_cover * edge * soak * wetness;
		// A lens this flat keeps no normal through the squash, so it is built here: the skin's normal, tipped out
		// towards the rim, and rivulets along the run that catch the light one at a time.
		float streak = sin(spot_local.z * 9.0 + sin(spot_local.x * 2.5) * 1.5);
		vec3 across = normalize(spot_across);
		NORMAL = normalize(normalize(spot_up) + normalize(spot_run) * (spot_local.x * 0.2) + across * (spot_local.z * 0.2 + streak * 0.25 * edge));
		// premultiplied by hand (the Mobile renderer cannot compile PREMUL_ALPHA_FACTOR)
		ALBEDO = tint.rgb * COLOR.rgb * 0.1 * cover;
		ROUGHNESS = mix(0.45, 0.05, wetness);
		METALLIC = 0.0;
		SPECULAR = 0.5 * edge * wetness;
		EMISSION = emission.rgb * cover;
		ALPHA = cover;
	}
	else
	{
		ALBEDO = tint.rgb * COLOR.rgb;
		ROUGHNESS = fluid_filtered_roughness(NORMAL, mix(fluid_roughness(wetness), 0.85, cure));
		METALLIC = 0.0;
		SPECULAR = 0.5;
		CLEARCOAT = clearcoat * wetness * (1.0 - cure);
		CLEARCOAT_ROUGHNESS = ROUGHNESS;
		BACKLIGHT = fluid_backlight(COLOR.rgb, 0.002);
		EMISSION = emission.rgb;
		ALPHA = 1.0;
	}
}
