shader_type canvas_item;

uniform vec4 glow_color : source_color = vec4(0.3, 0.6, 1.0, 0.5);
uniform float glow_intensity : hint_range(0.0, 3.0) = 1.5;
uniform float glow_size : hint_range(0.0, 5.0) = 2.0;

void fragment() {
    vec2 size = vec2(textureSize(TEXTURE, 0));
    vec2 uv = SCREEN_UV;
    
    // Calculate distance to the border
    float dist_x = min(uv.x, 1.0 - uv.x) * size.x;
    float dist_y = min(uv.y, 1.0 - uv.y) * size.y;
    float dist = min(dist_x, dist_y);
    
    // Create a glow effect that fades with distance from border
    float glow_factor = smoothstep(0.0, glow_size * 10.0, dist) * (1.0 - smoothstep(glow_size * 10.0, glow_size * 20.0, dist));
    glow_factor = glow_factor * glow_intensity;
    
    // Apply the glow
    vec4 final_color = glow_color;
    final_color.a = final_color.a * glow_factor;
    
    COLOR = final_color;
}