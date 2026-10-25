// Unlit, two looks blended by how far the surface is from a point: near_texture times near_color inside
// lerp_distance of lerp_point, far_texture times far_color beyond it, crossing over a band transition_range
// wide. The point is in world space, or in the mesh's own space with local_space. A texture left unset reads as
// white.
//
// The crossover ratio is (distance past the edge) / band + band / 2, which starts the far look slightly inside
// the edge. That is the curve a package's content was authored against, so it is kept.
//
// Mat_UnlitDistanceLerp is the opaque file; every other state is source the material writes over this body
// (SpecialMaterialSource). Defines a variant sets before including:
//   TRANSPARENT_PASS  writes ALPHA
//   BLEND_ADDITIVE    adds the colour whatever its alpha
//   ALPHA_CUTOUT      discards under alpha_cutoff
//   ZTEST_NEVER       draws nothing
//   DEPTH_OFFSET      writes a biased depth, see DepthOffset.gdshaderinc
// -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform vec3 lerp_point = vec3(0.0);
uniform float lerp_distance = 2.0;
uniform float transition_range = 0.2;
uniform bool local_space = false;

uniform sampler2D near_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_near_texture = false;
uniform vec2 near_texture_scale = vec2(1.0);
uniform vec2 near_texture_offset = vec2(0.0);
uniform vec4 near_color : source_color = vec4(1.0);

uniform sampler2D far_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_far_texture = false;
uniform vec2 far_texture_scale = vec2(1.0);
uniform vec2 far_texture_offset = vec2(0.0);
uniform vec4 far_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

varying vec3 lerp_position;

void vertex() {
	lerp_position = local_space ? VERTEX : (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
#ifdef ZTEST_NEVER
	discard;
#endif
	// A zero band is a hard step at the edge, which a division by zero saturating would have given.
	float band = abs(transition_range) < 1e-6 ? 1e-6 : transition_range;
	float past_edge = distance(lerp_position, lerp_point) - lerp_distance;
	float t = clamp(past_edge / band + band * 0.5, 0.0, 1.0);

	vec4 near_look = near_color;
	if (use_near_texture) {
		near_look *= texture(near_texture, UV * near_texture_scale + near_texture_offset);
	}
	vec4 far_look = far_color;
	if (use_far_texture) {
		far_look *= texture(far_texture, UV * far_texture_scale + far_texture_offset);
	}
	vec4 c = mix(near_look, far_look, t);

#ifdef ALPHA_CUTOUT
	if (c.a < alpha_cutoff) {
		discard;
	}
#endif

	ALBEDO = c.rgb;
#ifdef TRANSPARENT_PASS
#ifdef BLEND_ADDITIVE
	ALPHA = 1.0;
#else
	ALPHA = c.a;
#endif
#endif

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
