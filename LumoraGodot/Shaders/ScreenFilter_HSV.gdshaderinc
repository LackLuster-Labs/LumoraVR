// Hue, saturation and value of whatever is behind the surface: each scaled by hsv_multiply, then
// shifted by hsv_offset. Hue wraps, saturation clamps, value is left free so HDR survives. The
// conversion pair works without clipping, so a package's offsets and multipliers land the same. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform vec3 hsv_offset = vec3(0.2);
uniform vec3 hsv_multiply = vec3(1.0);

vec3 rgb_to_hsv_no_clip(vec3 rgb) {
	float max_channel = max(rgb.r, rgb.g);
	float min_channel = min(rgb.r, rgb.g);
	max_channel = max(max_channel, rgb.b);
	min_channel = min(min_channel, rgb.b);

	vec3 hsv = vec3(0.0, 0.0, max_channel);
	float delta = max_channel - min_channel;
	if (delta != 0.0) {
		hsv.y = delta / hsv.z;
		vec3 del = (hsv.zzz - rgb + 3.0 * delta) / (6.0 * delta);
		if (rgb.r == hsv.z) {
			hsv.x = del.b - del.g;
		} else if (rgb.g == hsv.z) {
			hsv.x = (1.0 / 3.0) + del.r - del.b;
		} else if (rgb.b == hsv.z) {
			hsv.x = (2.0 / 3.0) + del.g - del.r;
		}
	}
	return hsv;
}

vec3 hsv_to_rgb(vec3 hsv) {
	float h = hsv.x * 6.0;
	float i = floor(h);
	float v1 = hsv.z * (1.0 - hsv.y);
	float v2 = hsv.z * (1.0 - hsv.y * (h - i));
	float v3 = hsv.z * (1.0 - hsv.y * (1.0 - (h - i)));
	if (i == 0.0) {
		return vec3(hsv.z, v3, v1);
	} else if (i == 1.0) {
		return vec3(v2, hsv.z, v1);
	} else if (i == 2.0) {
		return vec3(v1, hsv.z, v3);
	} else if (i == 3.0) {
		return vec3(v1, v2, hsv.z);
	} else if (i == 4.0) {
		return vec3(v3, v1, hsv.z);
	}
	return vec3(hsv.z, v1, v2);
}

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;

	vec3 hsv = rgb_to_hsv_no_clip(behind);
	hsv *= hsv_multiply;
	hsv += hsv_offset;
	hsv.x = fract(hsv.x);
	hsv.y = clamp(hsv.y, 0.0, 1.0);

	ALBEDO = filter_output(hsv_to_rgb(hsv), behind);
	ALPHA = filter_output_alpha();
}
