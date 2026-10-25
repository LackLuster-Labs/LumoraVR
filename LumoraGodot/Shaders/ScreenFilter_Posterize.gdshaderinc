// Posterizes whatever is behind the surface: each channel rounded to the nearest step of 1 / levels, so a
// smooth sky comes out in bands. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform float levels = 10.0;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	float steps = max(levels, 0.0001);
	ALBEDO = filter_output(round(behind * steps) / steps, behind);
	ALPHA = filter_output_alpha();
}
