// Shared body of the fresnel lerp: two complete fresnel sets, crossfaded by lerp_amount. Set 0 at 0, set 1 at 1.
//
// Mat_FresnelLerp is the blended, depth pre-passed, back-culled state. Another blend, culling, depth write or a
// depth bias comes as generated source over this body (FresnelLerpMaterial). Defines it can set:
//   TRANSPARENT_PASS  carries alpha through; without it the surface stays in the opaque pass
//   DEPTH_OFFSET      writes a biased depth, see DepthOffset.gdshaderinc
// -xlinka

#include "res://Shaders/PolarUV.gdshaderinc"
#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform float lerp_amount : hint_range(0.0, 1.0) = 0.0;
// Optional per-pixel crossfade on top of the scalar. Read off the red channel and multiplied in, so a
// black texture pins the surface to set 0 regardless of the scalar.
uniform sampler2D lerp_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_lerp_texture = false;
uniform vec2 lerp_texture_scale = vec2(1.0);
uniform vec2 lerp_texture_offset = vec2(0.0);
// Reads the lerp texture wrapped around the centre of the UV square instead, under the same tiling.
uniform bool lerp_texture_polar = false;
uniform float lerp_texture_polar_power = 1.0;

// Near = facing the viewer, far = at a grazing angle. The exponent shapes how fast one becomes the
// other; 1 is a straight ramp, higher values pin the effect to the silhouette. The gamma curve bends
// both sets after the exponent.
uniform float exponent0 : hint_range(0.1, 16.0) = 1.0;
uniform float exponent1 : hint_range(0.1, 16.0) = 1.0;
uniform float gamma_curve = 1.0;

uniform vec4 near_color0 : source_color = vec4(1.0);
uniform vec4 far_color0 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 near_color1 : source_color = vec4(0.8, 0.8, 0.8, 1.0);
uniform vec4 far_color1 : source_color = vec4(0.2, 0.2, 0.2, 1.0);

uniform sampler2D near_texture0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_near_texture0 = false;
uniform sampler2D far_texture0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_far_texture0 = false;
uniform sampler2D near_texture1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_near_texture1 = false;
uniform sampler2D far_texture1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_far_texture1 = false;

// Each set's own surface detail, bending the normal its fresnel is read from.
uniform sampler2D normal_map0 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map0 = false;
uniform sampler2D normal_map1 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map1 = false;

uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);
uniform bool use_vertex_color = false;

varying vec2 lerp_uv;
varying vec2 mesh_uv0;

vec3 lerp_set_normal(vec3 n, vec3 tangent, vec3 binormal, sampler2D map, vec2 uv) {
	vec3 tn = texture(map, uv).xyz * 2.0 - 1.0;
	return normalize(tangent * tn.x + binormal * tn.y + n * tn.z);
}

void vertex() {
	mesh_uv0 = UV;
	lerp_uv = UV * lerp_texture_scale + lerp_texture_offset;
	UV = UV * texture_scale + texture_offset;
}

void fragment() {
	vec3 v = normalize(VIEW);
	vec3 n0 = normalize(NORMAL);
	vec3 n1 = n0;
	if (use_normal_map0) {
		n0 = lerp_set_normal(NORMAL, TANGENT, BINORMAL, normal_map0, UV);
	}
	if (use_normal_map1) {
		n1 = lerp_set_normal(NORMAL, TANGENT, BINORMAL, normal_map1, UV);
	}

	vec4 near0 = near_color0;
	if (use_near_texture0) { near0 *= texture(near_texture0, UV); }
	vec4 far0 = far_color0;
	if (use_far_texture0) { far0 *= texture(far_texture0, UV); }
	float rim0 = 1.0 - clamp(dot(n0, v), 0.0, 1.0);
	vec4 set0 = mix(near0, far0, pow(pow(rim0, exponent0), gamma_curve));

	vec4 near1 = near_color1;
	if (use_near_texture1) { near1 *= texture(near_texture1, UV); }
	vec4 far1 = far_color1;
	if (use_far_texture1) { far1 *= texture(far_texture1, UV); }
	float rim1 = 1.0 - clamp(dot(n1, v), 0.0, 1.0);
	vec4 set1 = mix(near1, far1, pow(pow(rim1, exponent1), gamma_curve));

	float t = lerp_amount;
	if (use_lerp_texture) {
		if (lerp_texture_polar) {
			vec2 grad_x;
			vec2 grad_y;
			vec2 p = polar_transform(polar_uv(mesh_uv0, lerp_texture_polar_power), lerp_texture_scale, lerp_texture_offset, grad_x, grad_y);
			t *= polar_sample(lerp_texture, p, grad_x, grad_y).r;
		} else {
			t *= texture(lerp_texture, lerp_uv).r;
		}
	}

	vec4 out_color = mix(set0, set1, clamp(t, 0.0, 1.0));
	if (use_vertex_color) {
		out_color *= COLOR;
	}

	ALBEDO = out_color.rgb;
#ifdef TRANSPARENT_PASS
	ALPHA = out_color.a;
#endif

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
