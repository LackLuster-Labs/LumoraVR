// Shared by the Gamma, Grayscale, Invert, Pixelate, Posterize and Depth filters: each reads what is BEHIND its
// surface from hint_screen_texture at SCREEN_UV and writes a changed copy over it.
//
// The pass is always the transparent one and the blend is always a plain replace, because the colour being
// blended onto is the very colour the filter already read: additive and multiply are done here against that
// read, not by the blend unit, and culling is a uniform for the same reason. What is behind means the opaque
// scene; transparent surfaces drawn earlier are not in the screen copy.
//
// The canvas state lives here too: clipping to a rect, the colour write mask, and drawing nothing at all,
// which a generated variant switches on (ScreenFilterShader). The mask needs no blend state of its own: a
// channel the filter may not write keeps what it read. With every colour channel masked the surface writes no
// colour and still lays down its depth and stencil, which is all such a surface is for. -xlinka

uniform sampler2D screen_behind : hint_screen_texture, filter_linear;

// Set by the material hook. 0 Opaque, 1 Cutout, 2 Alpha, 3 Transparent, 4 Additive, 5 Multiply.
uniform int blend_mode = 0;
// Set by the material hook. 0 culls back faces, 1 front faces, 2 neither.
uniform int cull_mode = 0;

// Set by the material: x and y in the mesh's own space, min then max. Outside it nothing draws.
uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);
// Set by the material. 1 red, 2 green, 4 blue, 8 alpha.
uniform int color_mask = 15;

varying vec2 filter_local;

void vertex() {
	filter_local = VERTEX.xy;
}

bool filter_skipped(bool front_facing) {
#ifdef SCREEN_FILTER_NEVER
	return true;
#else
	if ((cull_mode == 0 && !front_facing) || (cull_mode == 1 && front_facing)) {
		return true;
	}
	return rect_clip && (any(lessThan(filter_local, rect.xy)) || any(greaterThan(filter_local, rect.zw)));
#endif
}

// The opaque scene behind carries full alpha, so alpha and premultiplied blending land on the filtered colour
// unchanged.
vec3 filter_output(vec3 filtered, vec3 behind) {
	vec3 blended = filtered;
	if (blend_mode == 4) {
		blended = behind + filtered;
	} else if (blend_mode == 5) {
		blended = behind * filtered;
	}
	vec3 written = vec3(
		(color_mask & 1) != 0 ? 1.0 : 0.0,
		(color_mask & 2) != 0 ? 1.0 : 0.0,
		(color_mask & 4) != 0 ? 1.0 : 0.0);
	return mix(behind, blended, written);
}

float filter_output_alpha() {
	return (color_mask & 7) == 0 ? 0.0 : 1.0;
}
