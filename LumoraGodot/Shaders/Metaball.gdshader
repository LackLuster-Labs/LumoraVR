shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

uniform vec4 tint_a : source_color = vec4(0.90, 1.00, 0.94, 1.0);
uniform vec4 tint_b : source_color = vec4(1.00, 0.58, 0.88, 1.0);
uniform vec4 water_deep_color : source_color = vec4(0.04, 0.03, 0.06, 1.0);
uniform vec4 water_surface_color : source_color = vec4(0.10, 0.085, 0.13, 1.0);
uniform vec4 ripple_color : source_color = vec4(0.72, 1.00, 0.92, 1.0);

uniform float blob_radius : hint_range(0.03, 0.6) = 0.12;
uniform float blob_smoothness : hint_range(0.0, 0.6) = 0.075;
uniform int blob_count : hint_range(1, 96) = 72;
uniform float rise_speed : hint_range(0.05, 2.0) = 0.34;
uniform vec2 volume_extents = vec2(24.5, 24.5);
uniform float volume_height : hint_range(0.5, 6.0) = 3.4;
uniform vec3 volume_offset = vec3(0.0, 0.5, 0.0);

uniform float rim_strength : hint_range(0.0, 4.0) = 1.55;
uniform float rim_falloff : hint_range(0.2, 6.0) = 2.8;
uniform float fresnel_power : hint_range(0.5, 5.0) = 3.0;
uniform float alpha_scale : hint_range(0.0, 2.0) = 1.0;
uniform float emission_strength : hint_range(0.0, 4.0) = 0.82;
uniform float time_scale : hint_range(0.0, 4.0) = 1.0;
uniform float wave_strength : hint_range(0.0, 2.0) = 0.28;
uniform float ripple_strength : hint_range(0.0, 2.0) = 0.62;
uniform float ripple_radius : hint_range(0.08, 2.0) = 0.58;
uniform float ripple_width : hint_range(0.008, 0.18) = 0.032;
uniform float line_strength : hint_range(0.0, 1.0) = 0.34;

varying vec3 world_pos;

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float hash11(float n) {
    return fract(sin(n) * 43758.5453123);
}

float blob_life(int i, float t) {
    float fi = float(i);
    float phase = hash11(fi * 8.213);
    return fract(t * rise_speed + phase);
}

// Polar disk layout. Must stay in lockstep with LocalHomeRising.gdshader's
// blob_center so ripples land directly under the rising orbs. - xlinka
vec3 blob_center(int i, float t, vec3 origin) {
    float fi = float(i);
    float r_rand = hash11(fi * 1.137 + 7.31);
    float a_rand = hash11(fi * 3.751 + 0.91);
    float disk_radius = min(volume_extents.x, volume_extents.y);
    float r = sqrt(r_rand) * disk_radius;
    float a = a_rand * 6.28318530718;
    float rx = cos(a) * r;
    float rz = sin(a) * r;
    float life = blob_life(i, t);
    float y = mix(-0.06, volume_height + 0.28, life);
    return origin + vec3(rx, y, rz);
}

float blob_ripples(vec2 p, float t, vec3 origin) {
    float ripples = 0.0;

    for (int i = 0; i < 96; i++) {
        if (i >= blob_count) {
            break;
        }

        float life = blob_life(i, t);
        vec3 center = blob_center(i, t, origin);
        float d = distance(p, center.xz);
        float lift = smoothstep(0.04, 0.42, life);
        float ring_radius = mix(blob_radius * 1.35, ripple_radius, lift);
        float width = ripple_width + blob_smoothness * 0.025;
        float ring = 1.0 - smoothstep(width * 0.35, width, abs(d - ring_radius));
        float contact = 1.0 - smoothstep(0.30, 0.72, life);
        float fade = smoothstep(0.0, 0.08, life) * (1.0 - smoothstep(0.80, 1.0, life));
        float center_pull = 1.0 - smoothstep(0.0, blob_radius * 1.8, d);
        ripples += ring * fade;
        ripples += center_pull * contact * 0.50;
    }

    return clamp(ripples, 0.0, 1.0);
}

float mesh_edge(vec2 local_p) {
    vec2 normalized = local_p / max(volume_extents, vec2(0.001));
    float radial = length(normalized);
    float edge = smoothstep(0.955, 0.988, radial) * (1.0 - smoothstep(1.0, 1.025, radial));
    return edge;
}

float wave_lines(vec2 p, float t) {
    float a = sin(p.x * 0.42 + sin(p.y * 0.18 + t * 0.38) * 1.35 + t * 0.36);
    float b = sin((p.x * 0.24 + p.y * 0.36) - t * 0.28 + sin(p.x * 0.10) * 0.9);
    float aa = max(fwidth(a) + fwidth(b), 0.012);
    float line_a = 1.0 - smoothstep(0.018, 0.018 + aa * 1.5, abs(a));
    float line_b = 1.0 - smoothstep(0.016, 0.016 + aa * 1.5, abs(b));
    float fade = 0.55 + 0.45 * sin(t * 0.22 + p.x * 0.035 + p.y * 0.025);
    return clamp((line_a * 0.70 + line_b * 0.42) * fade, 0.0, 1.0);
}

void fragment() {
    vec3 floor_world = world_pos;
    vec3 origin = MODEL_MATRIX[3].xyz + volume_offset;
    float t = TIME * time_scale;
    vec2 local_p = floor_world.xz - origin.xz;

    float edge = mesh_edge(local_p);
    float waves = wave_lines(floor_world.xz, t) * wave_strength;
    float ripples = blob_ripples(floor_world.xz, t, origin) * ripple_strength;
    float line_mask = clamp(edge * 1.15 + waves * 0.40 + ripples * 0.80, 0.0, 1.0);
    float tint_mix = clamp(waves * 0.45 + ripples * 0.35, 0.0, 1.0);

    vec3 final_rgb = mix(ripple_color.rgb, tint_b.rgb, tint_mix);
    vec3 final_emission = final_rgb * (0.38 + line_mask * 1.10) * emission_strength * line_strength;
    float alpha = clamp(line_mask * (0.50 + line_strength * 0.35), 0.0, 0.82) * alpha_scale;

    ALBEDO = final_rgb;
    EMISSION = final_emission;
    ALPHA = alpha;
    ROUGHNESS = 0.35;
    METALLIC = 0.0;
}
