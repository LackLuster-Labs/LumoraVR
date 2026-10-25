// Pixelates whatever is behind the surface: the screen cut into resolution blocks across and down, each
// showing the one colour at its centre. A resolution texture scales the count over the surface, red across
// and green down, so the blocks can grow toward an edge.
//
// Unlike the other filters this one writes depth when opaque, so transparent surfaces behind it stay hidden
// rather than drawing unpixelated over it. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform vec2 resolution = vec2(100.0);
uniform sampler2D resolution_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_resolution_texture = false;
uniform vec4 resolution_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec2 blocks = resolution;
	if (use_resolution_texture) {
		blocks *= texture(resolution_texture, UV * resolution_texture_transform.xy + resolution_texture_transform.zw).rg;
	}
	blocks = max(abs(blocks), vec2(0.0001));

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	vec3 block = texture(screen_behind, round(SCREEN_UV * blocks) / blocks).rgb;
	ALBEDO = filter_output(block, behind);
	ALPHA = filter_output_alpha();
}
