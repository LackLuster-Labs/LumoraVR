shader_type sky;

uniform vec4 top_color : source_color = vec4(0.06, 0.16, 0.32, 1.0);
uniform vec4 horizon_color : source_color = vec4(0.38, 0.74, 0.90, 1.0);
uniform vec4 bottom_color : source_color = vec4(0.015, 0.045, 0.08, 1.0);
uniform vec4 sun_color : source_color = vec4(1.0, 0.82, 0.48, 1.0);
uniform vec3 sun_direction = vec3(-0.38, 0.58, -0.72);
uniform float sun_size : hint_range(0.004, 0.12) = 0.032;
uniform float sun_intensity : hint_range(0.0, 8.0) = 1.8;
uniform float sun_glow_power : hint_range(8.0, 256.0) = 96.0;

void sky() {
    vec3 view_dir = normalize(EYEDIR);
    vec3 sun_dir = normalize(sun_direction);
    float height = clamp(view_dir.y * 0.5 + 0.5, 0.0, 1.0);

    vec3 lower = mix(bottom_color.rgb, horizon_color.rgb, smoothstep(0.0, 0.58, height));
    vec3 sky_col = mix(lower, top_color.rgb, smoothstep(0.42, 1.0, height));

    float sun_dot = clamp(dot(view_dir, sun_dir), 0.0, 1.0);
    float disc = smoothstep(cos(sun_size), 1.0, sun_dot);
    float glow = pow(sun_dot, sun_glow_power * 0.22) * 0.18;

    COLOR = sky_col + sun_color.rgb * (disc + glow) * sun_intensity;
}
