// Cuts whatever is behind the surface to two levels per channel at threshold, with a ramp transition
// wide between them: (c - threshold) / transition, nudged up by half the transition and clamped, so a
// package's threshold and transition land the same. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform float threshold = 0.5;
uniform float transition = 0.005;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;

	vec3 c = behind - threshold;
	c /= transition;
	c += transition * 0.5;
	c = clamp(c, vec3(0.0), vec3(1.0));

	ALBEDO = filter_output(c, behind);
	ALPHA = filter_output_alpha();
}
