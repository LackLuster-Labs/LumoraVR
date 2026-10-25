shader_type canvas_item;
// The chat box's capture over the window. The capture's colour arrives multiplied by its own coverage; where
// a card or the pane covers the frame behind it, that frame shows softened and pulled toward a dark tint, so
// the lines stay readable over a bright sky without the box being a solid block. opacity fades the whole of
// it, glass and all, as the box goes quiet. Mixed in linear like the dash composite, since the window canvas
// is sRGB. - xlinka
render_mode unshaded, blend_mix;

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
uniform float opacity : hint_range(0.0, 1.0) = 1.0;
uniform float blur_radius : hint_range(0.0, 16.0) = 2.0;
uniform vec4 tint_color = vec4(0.0023, 0.0023, 0.004, 1.0);
uniform float tint_strength : hint_range(0.0, 1.0) = 0.5;
uniform float glass_from : hint_range(0.01, 1.0) = 0.1;

vec3 to_linear(vec3 c) {
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec3 to_srgb(vec3 c) {
	c = clamp(c, 0.0, 1.0);
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

vec3 tap(vec2 uv) {
	return to_linear(texture(screen_tex, uv).rgb);
}

vec3 soften(vec2 uv, vec2 px) {
	vec2 o = px * blur_radius;
	vec3 sum = tap(uv) * 4.0;
	sum += (tap(uv + vec2(o.x, 0.0)) + tap(uv - vec2(o.x, 0.0)) + tap(uv + vec2(0.0, o.y)) + tap(uv - vec2(0.0, o.y))) * 2.0;
	sum += tap(uv + o) + tap(uv - o) + tap(uv + vec2(o.x, -o.y)) + tap(uv + vec2(-o.x, o.y));
	return sum / 16.0;
}

void fragment() {
	vec4 ui = texture(TEXTURE, UV);
	if (ui.a <= 0.001 || opacity <= 0.001) {
		discard;
	}
	float glass = clamp(ui.a / glass_from, 0.0, 1.0);
	float uncovered = 1.0 - ui.a;
	vec3 behind = vec3(0.0);
	if (uncovered > 0.001) {
		behind = mix(soften(SCREEN_UV, SCREEN_PIXEL_SIZE), tint_color.rgb, tint_strength);
	}
	float cover = ui.a + glass * uncovered;
	COLOR = vec4(to_srgb((to_linear(ui.rgb) + behind * uncovered * glass) / cover), cover * opacity);
}
