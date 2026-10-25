// Gamma over whatever is behind the surface: each channel raised to the power gamma. Above 1 sinks the
// mid tones toward black, below 1 lifts them; black and white stay where they are. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform float gamma = 2.2;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	ALBEDO = filter_output(pow(max(behind, vec3(0.0)), vec3(gamma)), behind);
	ALPHA = filter_output_alpha();
}
