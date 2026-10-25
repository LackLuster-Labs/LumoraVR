shader_type spatial;
// The ring round a staff member's nameplate: their cyan on one side turning into a deep violet on the other,
// the split slowly turning, two comets with a white head and a long cyan tail chasing each other, and the
// whole ring breathing a little. The angle is taken in the ring's own space, so it runs the same from any
// side and in mirrors and cameras, and one material serves every staff plate in the world. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 glint_color : source_color = vec4(1.0);
uniform vec4 tail_color : source_color = vec4(0.55, 0.96, 1.0, 1.0);
uniform vec4 deep_color : source_color = vec4(0.5, 0.28, 1.0, 1.0);
uniform float laps_per_second = 0.3;
// over 1 so the ring lights the glow around it: the plate's own light, not a line drawn on it
uniform float brightness = 1.5;

varying vec2 ring_pos;

void vertex() {
	ring_pos = VERTEX.xy;
}

void fragment() {
	float turn = atan(ring_pos.y, ring_pos.x) / 6.2831853 + 0.5;
	float split = 0.5 + 0.5 * sin((turn + TIME * 0.06) * 6.2831853);
	vec3 base = mix(COLOR.rgb, deep_color.rgb, smoothstep(0.15, 0.85, split));

	float behind = fract(TIME * laps_per_second - turn);
	float behind2 = fract(behind + 0.5);
	float head = pow(1.0 - behind, 40.0) + pow(1.0 - behind2, 40.0);
	float tail = pow(1.0 - behind, 6.0) + pow(1.0 - behind2, 6.0);
	float breath = 0.85 + 0.15 * sin(TIME * 1.6);

	vec3 lit = mix(base * breath, tail_color.rgb, clamp(tail * 0.7, 0.0, 1.0));
	ALBEDO = mix(lit, glint_color.rgb * 1.4, clamp(head, 0.0, 1.0)) * brightness;
	ALPHA = clamp(max(COLOR.a, head), 0.0, 1.0);
}
