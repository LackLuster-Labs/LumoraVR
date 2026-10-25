// Overlay unlit, blended. The body the three cull variants share.
//
// Never depth-tested and never depth-written, so it stays on top of the scene. It reads the opaque pass's
// depth back to tell what is in front from what is behind: the front colour where nothing covers it, the
// behind colour where the scene does, which is how a handle stays findable through the object it sits in
// and still reads as behind it. Surfaces that never wrote depth (other overlays, the sky) do not cover.
// The colour is clamped to one: a blended overlay paints over the scene and never feeds the glow pass as
// light. -xlinka
//
// A depth bias (offset_factor, offset_units) moves where the surface sits for that test, the way it would
// move a depth-tested surface against what is behind it. -xlinka

#include "res://Shaders/PolarUV.gdshaderinc"
#include "res://Shaders/VertexColorSpace.gdshaderinc"
#include "res://Shaders/DepthOffset.gdshaderinc"

uniform vec4 front_color : source_color = vec4(1.0);
uniform vec4 behind_color : source_color = vec4(0.5, 0.5, 0.5, 0.5);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;

// The behind state's own texture; without one it reads the front's.
uniform sampler2D behind_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_behind_texture = false;

uniform vec2 front_uv_scale = vec2(1.0);
uniform vec2 front_uv_offset = vec2(0.0);
uniform vec2 behind_uv_scale = vec2(1.0);
uniform vec2 behind_uv_offset = vec2(0.0);
uniform bool use_vertex_color = true;

// Wraps the textures around the centre of the UV square, each under its own tiling.
uniform bool polar_mapping = false;
uniform float polar_power = 1.0;

// Set by the material hook. Cutout (1) drops what falls under the cutoff, before the vertex colour.
uniform int blend_mode = 2;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

// Metres of slack on the occlusion test, so a surface resting on the geometry behind it does not flicker
// between the two colours.
uniform float depth_bias : hint_range(0.0, 0.5) = 0.005;

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;

// One renderer's own, on this machine only (MeshRenderer.SetInstanceParams): xyz scales the mesh in its own
// space and w scales its alpha. The gizmo thins and fades the handle being dragged with it. Left alone it is
// all ones and changes nothing.
instance uniform vec4 instance_params = vec4(1.0);

void vertex() {
	VERTEX *= instance_params.xyz;
	COLOR = vertex_color_encode(COLOR);
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

	vec2 scale = behind ? behind_uv_scale : front_uv_scale;
	vec2 offset = behind ? behind_uv_offset : front_uv_offset;
	bool own_behind = behind && use_behind_texture;
	vec4 tex = vec4(1.0);
	if (use_albedo_texture || own_behind) {
		if (polar_mapping) {
			vec2 grad_x;
			vec2 grad_y;
			vec2 p = polar_transform(polar_uv(UV, polar_power), scale, offset, grad_x, grad_y);
			tex = own_behind ? polar_sample(behind_texture, p, grad_x, grad_y) : polar_sample(albedo_texture, p, grad_x, grad_y);
		} else {
			vec2 uv = UV * scale + offset;
			tex = own_behind ? texture(behind_texture, uv) : texture(albedo_texture, uv);
		}
	}
	vec4 out_color = (behind ? behind_color : front_color) * tex;

	if (blend_mode == 1 && out_color.a < alpha_cutoff) {
		discard;
	}

	if (use_vertex_color) {
		out_color *= vertex_color_decode(COLOR);
	}

	ALBEDO = clamp(out_color.rgb, vec3(0.0), vec3(1.0));
	ALPHA = clamp(out_color.a * instance_params.w, 0.0, 1.0);
}
