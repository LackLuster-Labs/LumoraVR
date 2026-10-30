// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The liquid body, shared by FluidLiquid.gdshader and its Near variant (FluidLiquidNear.gdshader), which
// defines FLUID_NEAR_EYE first.
//
// How much liquid the view crosses is the smaller of two: the gap to the opaque scene behind (right for a pool on
// a floor or a film on skin) and the liquid's own depth under this point (CUSTOM0.x) across the view ray (right for
// a stream or a drop in the air, whose gap is the whole sky). Only the gap was used once, so water against the sky
// absorbed everything and drew black. The scene behind is read through the bent ray, dimmed by Beer-Lambert in the
// fluid's own colour, and comes out as emission; reflection and highlights are the renderer's own specular (the
// sky's radiance and the lights) on a surface with no diffuse of its own, plus what a short march over the scene's
// depth finds mirrored (the legs standing in a pool), which the sky's reflection knows nothing of. Written
// premultiplied: a thin film or a thin stream lets the frame behind show through as drawn, so transparent things
// under a puddle (a grid's lines) still show; deep water replaces it with the bent view. A film lying on something
// darkens it a little, as wet things look. Moving water ripples; still water is a mirror. -xlinka

#ifdef FLUID_NEAR_EYE
#include "res://Modules/Fluids/Shaders/FluidNearEye.gdshaderinc"
#endif

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest, repeat_disable;

uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// extinction per cm, per channel; all zero takes it from the fluid's own colour over colour_depth
uniform vec4 absorption = vec4(0.0, 0.0, 0.0, 0.0);
// cm through which the fluid shows its own colour in full, when absorption is zero
uniform float colour_depth = 12.0;
uniform float gloss : hint_range(0.0, 1.0) = 0.95;
// how far the scene behind shifts, in m per m of liquid crossed; 0 = straight through (the light tiers)
uniform float refraction = 0.3;
// how dark a surface under a film reads (1 = not at all)
uniform float wet_darkening : hint_range(0.0, 1.0) = 0.8;
// 1 mirrors the scene where a march over its depth finds it; 0 = the sky's reflection only (the light tiers)
uniform float scene_reflection = 1.0;
#define FLUID_REFLECTION_STEPS 14
uniform float foam : hint_range(0.0, 1.0) = 0.0;
uniform vec4 emission : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform int debug_view = 0;

varying vec4 fluid_custom;
varying vec3 fluid_world;
varying float fluid_deep;

void vertex()
{
	fluid_custom = CUSTOM0;
	// The marker is metadata, not a metre of water to interpolate across a triangle. -xlinka
	fluid_deep = CUSTOM0.x >= 0.99 ? 1.0 : 0.0;
	fluid_custom.x = mix(max(CUSTOM0.x, 0.0), 0.06, fluid_deep);
	fluid_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// The opaque scene's view-space z at a screen point.
float fluid_scene_z(vec2 uv, mat4 inv_projection)
{
	float depth = texture(depth_tex, uv).r;
	vec4 v = inv_projection * vec4(uv * 2.0 - 1.0, depth, 1.0);
	return v.z / v.w;
}

vec2 fluid_screen_of(vec3 view, mat4 projection, bool ortho)
{
	vec4 clip = projection * vec4(view, 1.0);
	return (ortho ? clip.xy : clip.xy / max(clip.w, 1e-5)) * 0.5 + 0.5;
}

// What the surface mirrors of the opaque scene: the reflected ray marched over the scene's depth in growing
// steps. The renderer's own reflection only knows the sky, so a pool would never show the legs standing in it.
// Returns the colour in rgb and how sure the hit is in a (0 = no hit, the sky's reflection stands).
vec4 fluid_scene_reflection(vec3 at, vec3 dir, mat4 projection, mat4 inv_projection, bool ortho)
{
	float stride = 0.03;
	vec3 p = at;
	for (int i = 0; i < FLUID_REFLECTION_STEPS; i++)
	{
		p += dir * stride;
		vec2 uv = fluid_screen_of(p, projection, ortho);
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || p.z >= -0.01)
			break;
		float scene = fluid_scene_z(uv, inv_projection);
		// the ray passed behind something solid, by less than this stride: that is what it hits
		if (scene > p.z && scene - p.z < stride * 1.5 + 0.02)
		{
			vec2 edge = smoothstep(vec2(0.0), vec2(0.08), uv) * smoothstep(vec2(0.0), vec2(0.08), vec2(1.0) - uv);
			float sure = edge.x * edge.y * (1.0 - float(i) / float(FLUID_REFLECTION_STEPS));
			return vec4(textureLod(screen_tex, uv, 0.0).rgb, sure);
		}
		stride *= 1.35;
	}
	return vec4(0.0);
}

void fragment()
{
#ifdef FLUID_NEAR_EYE
	if (fluid_near_eye_cut(VERTEX, EYE_OFFSET, FRAGCOORD.xy))
	{
		discard;
	}
#endif
	float speed = fluid_custom.y;
	vec3 colour = clamp(tint.rgb * COLOR.rgb, vec3(0.02), vec3(1.0));
	// per cm
	vec3 extinction = dot(absorption.rgb, vec3(1.0)) > 0.0 ? absorption.rgb : -log(colour) / max(colour_depth, 0.01);

	// ripples in world space, only while it moves
	vec3 n = normalize(NORMAL);
	if (speed > 0.05)
	{
		vec3 wn = normalize((INV_VIEW_MATRIX * vec4(n, 0.0)).xyz);
		vec3 q = fluid_world * 180.0;
		float t = TIME * (1.0 + speed * 3.0);
		vec3 g = vec3(sin(q.y * 1.3 + q.z * 0.7 + t), sin(q.z * 1.1 + q.x * 0.9 - t * 0.7), sin(q.x * 1.2 + q.y * 0.8 + t * 0.5));
		g -= wn * dot(g, wn);
		wn = normalize(wn + g * (0.05 * clamp(speed, 0.0, 2.0)));
		n = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);
	}

	bool ortho = PROJECTION_MATRIX[3][3] != 0.0;
	vec3 ray = ortho ? vec3(0.0, 0.0, -1.0) : normalize(VERTEX);
	float along = ortho ? 1.0 : length(VERTEX) / max(-VERTEX.z, 1e-5);
	float gap = max(VERTEX.z - fluid_scene_z(SCREEN_UV, INV_PROJECTION_MATRIX), 0.0) * along;
	float facing = clamp(dot(n, VIEW), 0.0, 1.0);
	// the liquid's own depth under this point, across the ray; a deep march (1 m) leaves it to the scene
	float own = max(fluid_custom.x, 0.0) / max(facing, 0.25);
	// The scene can be the sky: an incomplete march must not absorb that entire distance. -xlinka
	float thickness = min(gap, mix(own, min(gap, 0.3), fluid_deep));

	// The scene behind along the bent ray, unless what is there stands in front of the liquid.
	vec2 uv = SCREEN_UV;
	if (refraction > 0.0)
	{
		vec3 bent = refract(ray, n, 0.75);
		vec3 target = VERTEX + (ray + (bent - ray) * (refraction / 0.25)) * min(thickness, 0.3);
		vec4 clip = PROJECTION_MATRIX * vec4(target, 1.0);
		vec2 moved = clamp(clip.xy / max(clip.w, 1e-5) * 0.5 + 0.5, vec2(0.0), vec2(1.0));
		if (ortho)
			moved = clamp(clip.xy * 0.5 + 0.5, vec2(0.0), vec2(1.0));
		if (fluid_scene_z(moved, INV_PROJECTION_MATRIX) < VERTEX.z)
			uv = moved;
	}
	vec3 behind = textureLod(screen_tex, uv, 0.0).rgb;
	vec3 through = exp(-extinction * (thickness * 100.0));
	// lying on something (the scene within a few cm behind) rather than hanging in front of it
	float lying = 1.0 - smoothstep(0.01, 0.05, gap);
	behind *= mix(1.0, wet_darkening, lying);
	float fresnel = 0.02 + 0.98 * pow(1.0 - facing, 5.0);
	float whitecap = foam * smoothstep(1.5, 4.0, speed);

	// How much of the view the bent, absorbed scene replaces: most of it under a few cm of liquid, less in a thin
	// film or a thin stream. The rest is the frame as drawn so far, straight through: the screen read only has the
	// opaque scene, so transparent things behind (a grid's lines under a pool) would vanish under full cover. The
	// reflection and the highlights add on top either way.
	float cover = clamp(max(thickness / 0.03, max(whitecap, 1.0 - through.b)), 0.25, 0.75);
	// the scene mirrored where the march finds it, in place of the sky's reflection there
	vec4 mirrored = scene_reflection > 0.5 && fresnel > 0.03 ? fluid_scene_reflection(VERTEX, reflect(ray, n), PROJECTION_MATRIX, INV_PROJECTION_MATRIX, ortho) : vec4(0.0);
	// premultiplied by hand (the Mobile renderer cannot compile PREMUL_ALPHA_FACTOR): the diffuse scales with the
	// cover, the specular stays whole
	ALBEDO = mix(colour * (vec3(1.0) - through) * 0.2, vec3(0.9), whitecap) * cover;
	EMISSION = behind * through * (1.0 - fresnel) * (1.0 - whitecap) * cover + mirrored.rgb * (fresnel * mirrored.a) + emission.rgb;
	NORMAL = n;
	ROUGHNESS = clamp(1.0 - gloss, 0.02, 1.0);
	METALLIC = 0.0;
	SPECULAR = 0.5 * (1.0 - mirrored.a);
	ALPHA = cover;
	if (debug_view > 0)
	{
		ALPHA = 1.0;
		// 1 the thickness used, 2 the gap to the scene, 3 the liquid's own depth across the ray (5 per metre);
		// 4 the scene read from behind as it is
		float shown = debug_view == 1 ? thickness : (debug_view == 2 ? gap : own);
		ALBEDO = vec3(0.0);
		EMISSION = debug_view == 4 ? textureLod(screen_tex, SCREEN_UV, 0.0).rgb : vec3(shown * 5.0);
		ROUGHNESS = 1.0;
		SPECULAR = 0.0;
	}
}
