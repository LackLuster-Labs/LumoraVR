shader_type spatial;
// The bar under a staff member's name: their colour with a glint sweeping along it, the bar's half of the
// light that crosses the name. Off the clock, one material for every staff plate. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 glint_color : source_color = vec4(0.88, 0.98, 1.0, 1.0);

void fragment() {
	float sweep = fract(TIME * 0.4) * 1.6 - 0.3;
	float d = (UV.x - sweep) / 0.14;
	float lit = exp(-d * d);
	ALBEDO = mix(COLOR.rgb, glint_color.rgb, lit * 0.85);
	ALPHA = COLOR.a;
}
