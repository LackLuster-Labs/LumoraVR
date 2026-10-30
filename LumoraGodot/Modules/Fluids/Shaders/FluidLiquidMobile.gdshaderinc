// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The mobile liquid body, shared by FluidLiquidMobile.gdshader and its Near variant (FluidLiquidMobileNear.gdshader),
// which defines FLUID_NEAR_EYE first.
//
// Without the screen, the liquid covers what is behind it by how much light the crossed liquid takes (its own depth
// under the point, CUSTOM0.x, across the view ray, in the fluid's own colour), plus a little for the wet look, and
// keeps its specular whole: written premultiplied, the sky's reflection and the highlights add on top while the
// scene behind shows through. A deep march (1 m) counts as a deep pool. -xlinka

#ifdef FLUID_NEAR_EYE
#include "res://Modules/Fluids/Shaders/FluidNearEye.gdshaderinc"
#endif

uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// extinction per cm, per channel; all zero takes it from the fluid's own colour over colour_depth
uniform vec4 absorption = vec4(0.0, 0.0, 0.0, 0.0);
// cm through which the fluid shows its own colour in full, when absorption is zero
uniform float colour_depth = 12.0;
uniform float gloss : hint_range(0.0, 1.0) = 0.95;
// how much of the scene even the thinnest film covers (it reads as wet)
uniform float film_cover : hint_range(0.0, 1.0) = 0.14;
// m of liquid a deep march counts as
uniform float deep = 0.06;
uniform float foam : hint_range(0.0, 1.0) = 0.0;
uniform vec4 emission : source_color = vec4(0.0, 0.0, 0.0, 1.0);

varying vec4 fluid_custom;

void vertex()
{
	fluid_custom = CUSTOM0;
	// Decode before interpolation to keep the deep marker out of the optical thickness. -xlinka
	fluid_custom.x = CUSTOM0.x >= 0.99 ? deep : max(CUSTOM0.x, 0.0);
}

void fragment()
{
#ifdef FLUID_NEAR_EYE
	if (fluid_near_eye_cut(VERTEX, EYE_OFFSET, FRAGCOORD.xy))
	{
		discard;
	}
#endif
	vec3 colour = clamp(tint.rgb * COLOR.rgb, vec3(0.02), vec3(1.0));
	vec3 extinction = dot(absorption.rgb, vec3(1.0)) > 0.0 ? absorption.rgb : -log(colour) / max(colour_depth, 0.01);
	float facing = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	float own = max(fluid_custom.x, 0.0);
	float thickness = min(own / max(facing, 0.25), 0.3);
	vec3 through = exp(-extinction * (thickness * 100.0));
	float taken = 1.0 - (through.r + through.g + through.b) / 3.0;
	float whitecap = foam * smoothstep(1.5, 4.0, fluid_custom.y);
	float cover = clamp(film_cover + (1.0 - film_cover) * taken + whitecap, 0.0, 1.0);
	// premultiplied by hand (this renderer cannot compile PREMUL_ALPHA_FACTOR): the diffuse scales with the cover,
	// the specular stays whole
	ALBEDO = mix(colour * 0.5, vec3(0.9), whitecap) * cover;
	ROUGHNESS = clamp(1.0 - gloss, 0.02, 1.0);
	METALLIC = 0.0;
	SPECULAR = 0.5;
	EMISSION = emission.rgb;
	ALPHA = cover;
}
