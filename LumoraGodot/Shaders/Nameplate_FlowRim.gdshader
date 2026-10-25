shader_type spatial;
// The ring of a tier whose name tag moves: the tier's own colour, as the accounts service sent it, carried
// round the plate with a warm gold and a violet flowing after it, and a spark circling the other way. The
// tier colour arrives as the vertex colour, so the tier file picks the hue and this only moves it. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 warm_color : source_color = vec4(1.0, 0.8, 0.36, 1.0);
uniform vec4 cool_color : source_color = vec4(0.6, 0.42, 1.0, 1.0);
uniform vec4 spark_color : source_color = vec4(1.0, 0.97, 0.9, 1.0);

varying vec2 ring_pos;

void vertex() {
	ring_pos = VERTEX.xy;
}

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
	float turn = atan(ring_pos.y, ring_pos.x) / 6.2831853 + 0.5;
	vec3 col = flow(COLOR.rgb, turn + TIME * 0.18);
	float behind = fract(turn + TIME * 0.25);
	float spark = pow(1.0 - behind, 24.0);
	float breath = 0.9 + 0.1 * sin(TIME * 2.1);
	ALBEDO = mix(col * breath, spark_color.rgb, spark * 0.9);
	ALPHA = clamp(max(COLOR.a, spark), 0.0, 1.0);
}
