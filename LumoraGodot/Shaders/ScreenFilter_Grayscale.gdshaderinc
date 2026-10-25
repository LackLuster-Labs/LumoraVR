// Grey over whatever is behind the surface: the channels weighed by grayscale_ratios into one brightness,
// then lerp_amount of the way from the colour to that grey. A gradient texture, when set, tones the grey
// instead, read across from dark at the left to bright at the right, so a sepia or a heat map is one strip.
//
// The strip is read along its bottom row, where the package reads it; a strip one pixel tall reads the same
// anywhere. It is clamped at both ends, so light past white holds the bright end. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform vec3 grayscale_ratios = vec3(0.3, 0.59, 0.11);
uniform float lerp_amount = 1.0;
uniform sampler2D gradient_texture : source_color, filter_linear, repeat_disable;
uniform bool use_gradient_texture = false;

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	float grey = dot(behind, grayscale_ratios);
	vec3 toned = use_gradient_texture ? textureLod(gradient_texture, vec2(grey, 1.0), 0.0).rgb : vec3(grey);
	ALBEDO = filter_output(mix(behind, toned, lerp_amount), behind);
	ALPHA = filter_output_alpha();
}
