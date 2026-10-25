shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled;

#include "res://Shaders/PolarUV.gdshaderinc"
#include "res://Shaders/VertexColorSpace.gdshaderinc"

uniform vec4 front_color : source_color = vec4(1.0);
uniform vec4 behind_color : source_color = vec4(0.5, 0.5, 0.5, 0.5);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
// The back faces' own texture; without one they read the front's.
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
uniform int blend_mode = 4;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

void vertex() {
	COLOR = vertex_color_encode(COLOR);
}

void fragment() {
	bool front = FRONT_FACING;
	vec2 scale = front ? front_uv_scale : behind_uv_scale;
	vec2 offset = front ? front_uv_offset : behind_uv_offset;
	bool own_behind = !front && use_behind_texture;
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
	vec4 out_color = (front ? front_color : behind_color) * tex;

	if (blend_mode == 1 && out_color.a < alpha_cutoff) {
		discard;
	}

	if (use_vertex_color) {
		out_color *= vertex_color_decode(COLOR);
	}

	ALBEDO = out_color.rgb;
	ALPHA = out_color.a;
}
