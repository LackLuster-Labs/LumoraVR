shader_type spatial;
// The body of a staff member's nameplate: night glass, indigo at the top sinking to deep navy at the bottom,
// a thin lit line along the top edge, and a slow band of light crossing it every few seconds. Every other
// plate is the theme's grey glass, so this is the block of colour that reads as staff from across a room.
// One material for every staff plate in the world, off the clock. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 top_color : source_color = vec4(0.17, 0.14, 0.46, 1.0);
uniform vec4 bottom_color : source_color = vec4(0.02, 0.05, 0.15, 1.0);
uniform vec4 edge_color : source_color = vec4(0.62, 0.9, 1.0, 1.0);
uniform vec4 sheen_color : source_color = vec4(0.55, 0.88, 1.0, 1.0);
uniform float body_alpha = 0.88;
uniform float sweep_seconds = 5.5;

void fragment() {
	float down = UV.y;
	vec3 body = mix(top_color.rgb, bottom_color.rgb, smoothstep(0.0, 1.0, down));

	float edge = smoothstep(0.1, 0.0, down) * 0.35;

	// a band leaning back a little, crossing left to right, then resting off the plate for a while
	float run = fract(TIME / sweep_seconds) * 2.6 - 0.8;
	float d = (UV.x + (0.5 - down) * 0.12 - run) / 0.07;
	float sheen = exp(-d * d) * 0.16;

	ALBEDO = body + edge_color.rgb * edge + sheen_color.rgb * sheen;
	ALPHA = clamp(body_alpha + sheen * 0.4, 0.0, 1.0);
}
