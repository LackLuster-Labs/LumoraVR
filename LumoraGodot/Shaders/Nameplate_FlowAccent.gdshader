shader_type spatial;
// The bar under a moving tier's name: the same three colours as its ring, flowing along the bar. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 warm_color : source_color = vec4(1.0, 0.8, 0.36, 1.0);
uniform vec4 cool_color : source_color = vec4(0.6, 0.42, 1.0, 1.0);

vec3 flow(vec3 tier, float t) {
	float p = fract(t) * 3.0;
	float k = smoothstep(0.0, 1.0, fract(p));
	if (p < 1.0) {
		return mix(tier, warm_color.rgb, k);
	}
	if (p < 2.0) {
		return mix(warm_color.rgb, cool_color.rgb, k);
	}
	return mix(cool_color.rgb, tier, k);
}

void fragment() {
	ALBEDO = flow(COLOR.rgb, UV.x * 0.8 - TIME * 0.3);
	ALPHA = COLOR.a;
}
