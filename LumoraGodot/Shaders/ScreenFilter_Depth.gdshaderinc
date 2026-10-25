// How far away whatever is behind the surface is, as grey: metres along the view, times depth_multiply, plus
// depth_offset, held to 0..1. A plain one shows the first metre in shades and everything past it white. With
// the clip range on, clip_start to clip_end is stretched over 0..1 first.
//
// Only the opaque scene has depth here, so transparent surfaces are looked through. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
uniform float depth_multiply = 1.0;
uniform float depth_offset = 0.0;
uniform bool use_clip_range = false;
uniform float clip_start = 0.0;
uniform float clip_end = 10.0;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	float raw = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	float distance_along = -(unprojected.z / unprojected.w);
	if (use_clip_range) {
		float span = clip_end - clip_start;
		distance_along = (distance_along - clip_start) / (abs(span) < 0.000001 ? 0.000001 : span);
	}
	float grey = clamp(distance_along * depth_multiply + depth_offset, 0.0, 1.0);

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	ALBEDO = filter_output(vec3(grey), behind);
	ALPHA = filter_output_alpha();
}
