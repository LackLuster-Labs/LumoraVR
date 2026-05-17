shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_front, unshaded;

uniform vec4 tint_a : source_color = vec4(0.72, 1.00, 0.92, 1.0);
uniform vec4 tint_b : source_color = vec4(1.00, 0.56, 0.88, 1.0);
uniform float blob_radius : hint_range(0.03, 0.6) = 0.12;
uniform float blob_smoothness : hint_range(0.0, 0.6) = 0.075;
uniform int blob_count : hint_range(1, 96) = 72;
uniform float rise_speed : hint_range(0.05, 2.0) = 0.34;
uniform vec2 volume_extents = vec2(24.0, 24.0);
uniform float volume_height : hint_range(0.5, 12.0) = 7.0;
uniform vec3 volume_offset = vec3(0.0, -3.5, 0.0);
uniform float rim_strength : hint_range(0.0, 4.0) = 1.55;
uniform float rim_falloff : hint_range(0.2, 6.0) = 2.8;
uniform float fresnel_power : hint_range(0.5, 5.0) = 3.0;
uniform float alpha_scale : hint_range(0.0, 2.0) = 0.92;
uniform float emission_strength : hint_range(0.0, 4.0) = 1.05;
uniform float time_scale : hint_range(0.0, 4.0) = 1.0;

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

// Per-bubble pop point in [0.62, 0.76]. Upper bound leaves head-room: the burst
// + vanish windows tack on another ~0.03 of life (~0.2m of rise) before the
// bubble is fully gone, and we want zero pops kissing the ceiling. Floor keeps
// pops past halfway up so none feel premature. Each index gets a stable value
// so the same bubble pops at the same height every cycle. - xlinka
float blob_pop_life(int i) {
    return mix(0.62, 0.76, hash11(float(i) * 5.197 + 2.13));
}

// Polar disk distribution so balls stay inside the cylindrical ground rather than
// poking past the rim like the old square layout did. sqrt(r) keeps density even. - xlinka
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
    float lift = smoothstep(0.06, 0.94, life);
    float y = mix(0.025, volume_height + 0.10, lift);
    return origin + vec3(rx, y, rz);
}

// Real-bubble pop, not a balloon deflation:
//   pre-pop window  -> surface tension wobble (high-freq jitter)
//   burst window    -> rapid outward expansion (~70%), no shrink
//   vanish window   -> alpha+size cut to zero a few frames later
// Fragment shader thins the alpha to a rim-only shell during burst so it
// reads as a popping soap film rather than a growing sphere. - xlinka
float blob_size(int i, float t) {
    float fi = float(i);
    float life = blob_life(i, t);
    float pop_life = blob_pop_life(i);
    float birth = smoothstep(0.02, 0.16, life);
    float wobble = 0.94 + 0.06 * sin(t * 2.2 + fi * 1.7);
    float separation = 0.72 + 0.28 * smoothstep(0.14, 0.36, life);

    float pre_pop = smoothstep(pop_life - 0.05, pop_life, life);
    float jitter = sin(t * 22.0 + fi * 5.1) * sin(t * 31.0 + fi * 2.3);
    float tension = 1.0 + pre_pop * jitter * 0.09;

    float burst = smoothstep(pop_life, pop_life + 0.022, life);
    float expansion = 1.0 + burst * 0.70;

    float vanish = smoothstep(pop_life + 0.012, pop_life + 0.032, life);
    float visible = 1.0 - vanish;

    return blob_radius * birth * wobble * separation * tension * expansion * visible;
}

float neck_size(int i, float t) {
    float life = blob_life(i, t);
    float birth = smoothstep(0.0, 0.10, life);
    float detach = 1.0 - smoothstep(0.24, 0.48, life);
    return blob_radius * 0.82 * birth * detach;
}

float ray_sphere_t(vec3 ro, vec3 rd, vec3 center, float radius, float near_t, float far_t) {
    if (radius < 0.004) {
        return 1e20;
    }

    vec3 oc = ro - center;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - radius * radius;
    float h = b * b - c;
    if (h < 0.0) {
        return 1e20;
    }

    h = sqrt(h);
    float t0 = -b - h;
    float t1 = -b + h;
    float hit_t = t0 >= near_t ? t0 : t1;

    if (hit_t < near_t || hit_t > far_t) {
        return 1e20;
    }

    return hit_t;
}

float ray_cylinder_y_t(vec3 ro, vec3 rd, vec2 center, float radius, float y_min, float y_max, float near_t, float far_t) {
    if (radius < 0.004 || y_max <= y_min) {
        return 1e20;
    }

    vec2 oc = ro.xz - center;
    float a = dot(rd.xz, rd.xz);
    if (a < 0.00001) {
        return 1e20;
    }

    float b = dot(oc, rd.xz);
    float c = dot(oc, oc) - radius * radius;
    float h = b * b - a * c;
    if (h < 0.0) {
        return 1e20;
    }

    h = sqrt(h);
    float t0 = (-b - h) / a;
    float t1 = (-b + h) / a;
    float hit_t = t0 >= near_t ? t0 : t1;
    float y = ro.y + rd.y * hit_t;

    if (hit_t < near_t || hit_t > far_t || y < y_min || y > y_max) {
        hit_t = t1;
        y = ro.y + rd.y * hit_t;
    }

    if (hit_t < near_t || hit_t > far_t || y < y_min || y > y_max) {
        return 1e20;
    }

    return hit_t;
}

vec2 ray_box(vec3 ro, vec3 rd, vec3 b_min, vec3 b_max) {
    vec3 safe_rd = vec3(
        abs(rd.x) < 0.00001 ? (rd.x < 0.0 ? -0.00001 : 0.00001) : rd.x,
        abs(rd.y) < 0.00001 ? (rd.y < 0.0 ? -0.00001 : 0.00001) : rd.y,
        abs(rd.z) < 0.00001 ? (rd.z < 0.0 ? -0.00001 : 0.00001) : rd.z
    );
    vec3 inv = 1.0 / safe_rd;
    vec3 t0 = (b_min - ro) * inv;
    vec3 t1 = (b_max - ro) * inv;
    vec3 tmin = min(t0, t1);
    vec3 tmax = max(t0, t1);
    float near_t = max(max(tmin.x, tmin.y), tmin.z);
    float far_t = min(min(tmax.x, tmax.y), tmax.z);
    return vec2(max(near_t, 0.0), far_t);
}

vec3 blob_color(vec3 p, float t) {
    float h = sin(p.y * 1.8 + t * 0.7) * 0.5 + 0.5;
    return mix(tint_a.rgb, tint_b.rgb, h);
}

void fragment() {
    vec3 frag_world = world_pos;
    vec3 cam_world = INV_VIEW_MATRIX[3].xyz;
    vec3 ro = cam_world;
    vec3 rd = normalize(frag_world - cam_world);

    vec3 origin = MODEL_MATRIX[3].xyz + volume_offset;
    vec3 b_min = origin + vec3(-volume_extents.x, 0.0, -volume_extents.y);
    vec3 b_max = origin + vec3(volume_extents.x, volume_height, volume_extents.y);
    vec2 bounds = ray_box(ro, rd, b_min, b_max);

    if (bounds.y < bounds.x) {
        discard;
    }

    float t = TIME * time_scale;
    float best_t = 1e20;
    vec3 best_center = vec3(0.0);
    float best_radius = 0.0;
    float best_life = 0.0;
    float best_pop = 0.94;
    int best_shape = 0;

    for (int i = 0; i < 96; i++) {
        if (i >= blob_count) {
            break;
        }

        float life = blob_life(i, t);
        float pop_life = blob_pop_life(i);
        vec3 center = blob_center(i, t, origin);
        float radius = blob_size(i, t);
        float sphere_t = ray_sphere_t(ro, rd, center, radius, bounds.x, bounds.y);
        if (sphere_t < best_t) {
            best_t = sphere_t;
            best_center = center;
            best_radius = radius;
            best_life = life;
            best_pop = pop_life;
            best_shape = 1;
        }

        float neck_radius = neck_size(i, t);
        // Cap the neck near the ground so taller volume_height doesn't stretch
        // the tail into a flagpole. - xlinka
        float neck_top = min(min(center.y, origin.y + volume_height), origin.y + 1.6);
        float neck_t = ray_cylinder_y_t(ro, rd, center.xz, neck_radius, origin.y - 0.02, neck_top, bounds.x, bounds.y);
        if (neck_t < best_t) {
            best_t = neck_t;
            best_center = vec3(center.x, origin.y, center.z);
            best_radius = neck_radius;
            best_life = life;
            best_pop = pop_life;
            best_shape = 2;
        }
    }

    if (best_t >= 1e19) {
        discard;
    }

    vec3 hit = ro + rd * best_t;
    vec3 n;
    if (best_shape == 2) {
        n = normalize(vec3(hit.x - best_center.x, best_radius * 0.22, hit.z - best_center.z));
    } else {
        n = normalize(hit - best_center);
    }
    vec3 v = normalize(ro - hit);
    float ndv = clamp(dot(n, v), 0.0, 1.0);
    float fres = pow(1.0 - ndv, fresnel_power);
    float rim = pow(1.0 - ndv, rim_falloff) * rim_strength;
    // During the burst, alpha collapses to a rim-only shell so the bubble reads
    // as a stretching soap film instead of a glowing sphere. Once vanish kicks
    // in, the whole thing dies. - xlinka
    float burst_t = smoothstep(best_pop, best_pop + 0.022, best_life);
    float vanish_t = smoothstep(best_pop + 0.012, best_pop + 0.032, best_life);
    float born = smoothstep(0.02, 0.14, best_life);
    float shell = mix(1.0, clamp(fres * 1.9 + 0.12, 0.0, 1.0), burst_t);
    float life_alpha = born * (1.0 - vanish_t) * shell;
    float neck_alpha = best_shape == 2 ? 0.70 : 1.0;
    // Sharp flash right at the rupture moment, peaks in a couple frames. - xlinka
    float pop_flash = best_shape == 1 ? exp(-pow((best_life - (best_pop + 0.003)) * 38.0, 2.0)) : 0.0;

    vec3 base = blob_color(hit, t);
    ALBEDO = base * (0.74 + n.y * 0.26) + vec3(rim);
    EMISSION = base * (0.24 + fres + pop_flash * 2.4) * emission_strength;
    ALPHA = clamp(alpha_scale * life_alpha * neck_alpha * (0.58 + fres * 0.42) + pop_flash * 0.45, 0.0, 1.0);
}
