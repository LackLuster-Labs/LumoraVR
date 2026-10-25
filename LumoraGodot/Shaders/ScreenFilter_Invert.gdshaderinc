// Inverts whatever is behind the surface, lerp_amount of the way from the colour to its opposite. Light past
// white inverts to black rather than below it. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform float lerp_amount = 1.0;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	vec3 inverted = max(vec3(1.0) - behind, vec3(0.0));
	ALBEDO = filter_output(mix(behind, inverted, lerp_amount), behind);
	ALPHA = filter_output_alpha();
}
