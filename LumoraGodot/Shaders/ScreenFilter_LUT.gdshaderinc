// Colour grade of whatever is behind the surface through a lookup table, and optionally a second
// table blended in by lut_lerp.
//
// THE TABLE IS A 2D STRIP: N slices of N by N laid side by side, N*N wide and N tall, red across a
// slice, green down it, blue picking the slice. The engine has no 3D textures, so a cube LUT arrives
// in the strip layout Godot's own colour correction takes, and blue is blended between the two nearest
// slices by hand.
//
// use_srgb reads the table with gamma-encoded coordinates and decodes what it returns, which is how
// a grade authored on an image is meant to be applied to a linear scene. Off, the table maps linear
// to linear. A table that is not there passes the colour through. -xlinka

#include "res://Shaders/ScreenFilter_Raster.gdshaderinc"

uniform sampler2D lut_texture : filter_linear, repeat_disable;
uniform bool use_lut_texture = false;
uniform sampler2D secondary_lut_texture : filter_linear, repeat_disable;
uniform bool use_secondary_lut_texture = false;
uniform float lut_lerp = 0.0;
uniform bool use_srgb = true;

vec3 linear_to_srgb(vec3 c) {
	c = clamp(c, vec3(0.0), vec3(1.0));
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

vec3 srgb_to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

// Texel-centred coordinates of colour c in a strip whose height is n, one per neighbouring slice.
vec4 strip_coords(vec3 c, vec2 size) {
	float n = size.y;
	float slice = c.b * (n - 1.0);
	float s0 = floor(slice);
	float s1 = min(s0 + 1.0, n - 1.0);
	vec2 inside = c.rg * (n - 1.0) + 0.5;
	return vec4(
		(s0 * n + inside.x) / size.x,
		inside.y / size.y,
		(s1 * n + inside.x) / size.x,
		slice - s0);
}

void fragment() {
	if (filter_skipped(FRONT_FACING)) {
		discard;
	}

	vec3 behind = texture(screen_behind, SCREEN_UV).rgb;
	vec3 coord = use_srgb ? linear_to_srgb(behind) : clamp(behind, vec3(0.0), vec3(1.0));

	vec3 c0 = behind;
	if (use_lut_texture) {
		vec4 at = strip_coords(coord, vec2(textureSize(lut_texture, 0)));
		vec3 graded = mix(textureLod(lut_texture, at.xy, 0.0).rgb, textureLod(lut_texture, vec2(at.z, at.y), 0.0).rgb, at.w);
		c0 = use_srgb ? srgb_to_linear(graded) : graded;
	}

	vec3 c = c0;
	if (lut_lerp > 0.0) {
		vec3 c1 = behind;
		if (use_secondary_lut_texture) {
			vec4 at = strip_coords(coord, vec2(textureSize(secondary_lut_texture, 0)));
			vec3 graded = mix(textureLod(secondary_lut_texture, at.xy, 0.0).rgb, textureLod(secondary_lut_texture, vec2(at.z, at.y), 0.0).rgb, at.w);
			c1 = use_srgb ? srgb_to_linear(graded) : graded;
		}
		c = mix(c0, c1, lut_lerp);
	}

	ALBEDO = filter_output(c, behind);
	ALPHA = filter_output_alpha();
}
