// Shared body of the overlay fresnel: a fresnel in two colour sets, Front where the surface is in view and
// Behind where the scene covers it, drawn with the depth test off so both show.
//
// The depth test is off, so "in front" and "behind" cannot come from the rasteriser. The opaque pass's depth
// is read back instead and view-space distances compared: a real occlusion answer, unlike splitting on face
// orientation. Surfaces that never wrote depth (other blended overlays, the sky) count as not occluding. A
// depth bias (offset_factor, offset_units) moves where the surface sits for that test. -xlinka
//
// Mat_OverlayFresnel is the additive, two-sided, never depth-written state. Other blends, culling and a depth
// write come as generated source over this body (OverlayFresnelMaterial); OVERLAY_OPAQUE writes full alpha.

#include "res://Shaders/PolarUV.gdshaderinc"
#include "res://Shaders/DepthOffset.gdshaderinc"

// Near = facing the viewer, far = at a grazing angle.
uniform float exponent : hint_range(0.1, 16.0) = 3.0;
// Bends the front set's falloff after the exponent; the behind set keeps the plain one.
uniform float gamma_curve = 1.0;
uniform vec4 front_near_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 front_far_color : source_color = vec4(1.0);
uniform vec4 behind_near_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 behind_far_color : source_color = vec4(0.25, 0.25, 0.25, 1.0);

// One texture shared by every colour, under the front or behind transform.
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 front_uv_scale = vec2(1.0);
uniform vec2 front_uv_offset = vec2(0.0);
uniform vec2 behind_uv_scale = vec2(1.0);
uniform vec2 behind_uv_offset = vec2(0.0);

// A colour's own texture under its own transform, in place of the shared one.
uniform sampler2D front_near_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_front_near_texture = false;
uniform vec2 front_near_texture_scale = vec2(1.0);
uniform vec2 front_near_texture_offset = vec2(0.0);
uniform sampler2D front_far_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_front_far_texture = false;
uniform vec2 front_far_texture_scale = vec2(1.0);
uniform vec2 front_far_texture_offset = vec2(0.0);
uniform sampler2D behind_near_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_behind_near_texture = false;
uniform vec2 behind_near_texture_scale = vec2(1.0);
uniform vec2 behind_near_texture_offset = vec2(0.0);
uniform sampler2D behind_far_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_behind_far_texture = false;
uniform vec2 behind_far_texture_scale = vec2(1.0);
uniform vec2 behind_far_texture_offset = vec2(0.0);

// Wraps every texture around the centre of the UV square, each under its own tiling.
uniform bool polar_mapping = false;
uniform float polar_power = 1.0;

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;

uniform bool use_vertex_color = false;

// Metres of slack on the occlusion test. Without it a surface resting exactly on the geometry behind
// it flickers between the two colour sets as depth precision wobbles.
uniform float depth_bias : hint_range(0.0, 0.5) = 0.005;

// The engine fills this one in; it is not a material parameter. Godot 4 dropped the DEPTH_TEXTURE
// builtin, so the opaque pass's depth has to be asked for by hint. Nearest filtering because an
// interpolated depth between two surfaces is a distance nothing is actually at.
uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;

vec4 overlay_sample(sampler2D tex, vec2 uv, vec2 scale, vec2 offset) {
	if (polar_mapping) {
		vec2 grad_x;
		vec2 grad_y;
		vec2 p = polar_transform(polar_uv(uv, polar_power), scale, offset, grad_x, grad_y);
		return polar_sample(tex, p, grad_x, grad_y);
	}
	return texture(tex, uv * scale + offset);
}

// Every branch here turns on a uniform, so all four colours sample in step and keep the derivatives.
vec4 overlay_layer(bool own, sampler2D own_texture, vec2 own_scale, vec2 own_offset, vec2 shared_scale, vec2 shared_offset, vec2 uv) {
	if (own) {
		return overlay_sample(own_texture, uv, own_scale, own_offset);
	}
	if (use_albedo_texture) {
		return overlay_sample(albedo_texture, uv, shared_scale, shared_offset);
	}
	return vec4(1.0);
}

void fragment() {
	float raw = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	float scene_dist = -(unprojected.z / unprojected.w);
	float frag_dist = -VERTEX.z;
	if (offset_factor != 0.0 || offset_units != 0.0) {
		vec4 biased = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, offset_depth(FRAGCOORD.z), 1.0);
		frag_dist = -(biased.z / biased.w);
	}
	bool behind = frag_dist > scene_dist + depth_bias;

	vec3 n = normalize(NORMAL);
	if (use_normal_map) {
		vec3 tn = texture(normal_map, UV).xyz * 2.0 - 1.0;
		n = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}
	float rim = 1.0 - clamp(dot(n, normalize(VIEW)), 0.0, 1.0);
	float f = pow(rim, exponent);
	if (!behind) {
		f = pow(f, gamma_curve);
	}

	vec4 near_color = behind ? behind_near_color : front_near_color;
	vec4 far_color = behind ? behind_far_color : front_far_color;
	vec4 out_color;
	if (use_front_near_texture || use_front_far_texture || use_behind_near_texture || use_behind_far_texture) {
		vec4 front_near = overlay_layer(use_front_near_texture, front_near_texture, front_near_texture_scale, front_near_texture_offset, front_uv_scale, front_uv_offset, UV);
		vec4 front_far = overlay_layer(use_front_far_texture, front_far_texture, front_far_texture_scale, front_far_texture_offset, front_uv_scale, front_uv_offset, UV);
		vec4 behind_near = overlay_layer(use_behind_near_texture, behind_near_texture, behind_near_texture_scale, behind_near_texture_offset, behind_uv_scale, behind_uv_offset, UV);
		vec4 behind_far = overlay_layer(use_behind_far_texture, behind_far_texture, behind_far_texture_scale, behind_far_texture_offset, behind_uv_scale, behind_uv_offset, UV);
		out_color = mix(near_color * (behind ? behind_near : front_near), far_color * (behind ? behind_far : front_far), f);
	} else {
		vec4 tex = vec4(1.0);
		if (use_albedo_texture) {
			tex = overlay_sample(albedo_texture, UV, behind ? behind_uv_scale : front_uv_scale, behind ? behind_uv_offset : front_uv_offset);
		}
		out_color = mix(near_color, far_color, f) * tex;
	}

	if (use_vertex_color) {
		out_color *= COLOR;
	}

	ALBEDO = out_color.rgb;
#ifdef OVERLAY_OPAQUE
	ALPHA = 1.0;
#else
	ALPHA = out_color.a;
#endif
}
