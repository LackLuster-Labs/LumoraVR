shader_type spatial;
render_mode blend_add, depth_draw_never, cull_back, unshaded;

uniform float emission_strength : hint_range(0.0, 6.0) = 1.4;

void fragment() {
    ALBEDO = COLOR.rgb;
    EMISSION = COLOR.rgb * emission_strength;
    ALPHA = COLOR.a;
}
