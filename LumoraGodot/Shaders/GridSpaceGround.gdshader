shader_type spatial;
render_mode cull_back;

// Glossy Aperture-style floor: light panels with thin blue accents, recessed
// seams (parallaxed deep layer + cell-inset darkening), low-roughness specular
// so the directional light kicks back as a clean highlight.
// Uniform names match GridSpaceGroundMaterial.cs. - xlinka

uniform vec4 base_near_color : source_color = vec4(0.045, 0.040, 0.035, 1.0);
uniform vec4 base_far_color : source_color = vec4(0.015, 0.013, 0.010, 1.0);
uniform vec4 line_near_color : source_color = vec4(0.34, 0.58, 0.98, 1.0);
uniform vec4 line_far_color : source_color = vec4(0.50, 0.72, 1.00, 1.0);
uniform float minor_scale : hint_range(0.1, 8.0) = 1.0;
uniform float major_scale : hint_range(1.0, 64.0) = 8.0;
uniform float line_width : hint_range(0.2, 2.5) = 1.0;
uniform float major_line_width : hint_range(0.4, 3.5) = 1.6;
uniform float radial_fade : hint_range(0.2, 3.0) = 1.1;
uniform float horizon_start : hint_range(1.0, 200.0) = 12.0;
uniform float horizon_end : hint_range(2.0, 400.0) = 48.0;
uniform float parallax_depth : hint_range(0.0, 1.0) = 0.55;
uniform float gloss : hint_range(0.0, 1.0) = 0.85;

varying vec3 world_pos;

void vertex()
{
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float grid_line(vec2 uv, float scale, float width_px)
{
    vec2 cell = uv / scale;
    vec2 deriv = max(fwidth(cell), vec2(0.0001));
    vec2 dist = abs(fract(cell - 0.5) - 0.5) / deriv;
    vec2 line_mask = vec2(1.0) - smoothstep(vec2(width_px), vec2(width_px + 1.0), dist);
    vec2 kill = vec2(1.0) - smoothstep(vec2(0.4), vec2(0.9), deriv);
    return max(line_mask.x * kill.x, line_mask.y * kill.y);
}

// Darkening inside each major cell that peaks at the seam and falls off
// quickly toward the center. Reads as a recessed panel edge. - xlinka
float seam_shadow(vec2 uv, float scale, float falloff_px)
{
    vec2 cell = uv / scale;
    vec2 deriv = max(fwidth(cell), vec2(0.0001));
    vec2 dist = abs(fract(cell - 0.5) - 0.5) / deriv;
    float e = min(dist.x, dist.y);
    float kill = 1.0 - smoothstep(0.4, 0.9, max(deriv.x, deriv.y));
    return (1.0 - smoothstep(0.0, falloff_px, e)) * kill;
}

void fragment()
{
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    vec3 to_cam = cam - world_pos;
    float cam_dist_xz = length(to_cam.xz);
    float total = max(length(to_cam), 0.001);

    // Parallax shift along the view direction, scaled by view obliqueness.
    // Stronger now (0.04 vs the old 0.006) so the deeper layer is visible at
    // grazing angles. - xlinka
    vec2 view_tangent = -to_cam.xz / total;
    float oblique = clamp(1.0 - abs(to_cam.y) / total, 0.0, 1.0);
    vec2 parallax_uv = UV + view_tangent * parallax_depth * 0.04 * oblique;

    float horizon = clamp((cam_dist_xz - horizon_start) / max(horizon_end - horizon_start, 0.001), 0.0, 1.0);
    float radial = clamp(length(UV - vec2(0.5)) * radial_fade, 0.0, 1.0);

    // Surface lines
    float minor = grid_line(UV, minor_scale, line_width);
    float major = grid_line(UV, major_scale, major_line_width);
    float grid_mask = clamp(max(minor * 0.35, major), 0.0, 1.0);

    // Deep parallax line layer (the "inside wall" of the seam, visible at oblique angles)
    float deep_line = grid_line(parallax_uv, major_scale, major_line_width * 0.7);
    float deep_shadow = seam_shadow(UV, major_scale, 5.0);

    vec3 panel_col = mix(base_near_color.rgb, base_far_color.rgb, clamp(radial * 0.40, 0.0, 1.0));

    // Recess: darken in a band along each major seam, then darken further at
    // the parallaxed deep line position. The combination reads as a sunken
    // panel with shadowed walls. - xlinka
    panel_col *= 1.0 - deep_shadow * 0.45;
    panel_col = mix(panel_col, panel_col * 0.35, deep_line * 0.55 * (1.0 - horizon));

    vec3 line_col = mix(line_near_color.rgb, line_far_color.rgb, radial);
    vec3 col = mix(panel_col, line_col, grid_mask);

    // Fresnel sheen so the floor catches light at grazing angles, gloss
    // controls how much. - xlinka
    vec3 view_dir = -to_cam / total;
    float fres = pow(1.0 - clamp(view_dir.y, 0.0, 1.0), 5.0);
    col += vec3(fres * gloss * 0.18) * (1.0 - horizon);

    // Horizon fade picks up a faint warm tint so the black floor doesn't
    // clash with the sunrise sky right at the seam line. - xlinka
    vec3 horizon_col = mix(base_near_color.rgb, vec3(0.10, 0.06, 0.04), 0.6);
    col = mix(col, horizon_col, horizon);

    ALBEDO = col;
    EMISSION = line_col * grid_mask * 0.22 * (1.0 - horizon);
    ROUGHNESS = mix(0.55, 0.15, gloss);
    METALLIC = 0.0;
    SPECULAR = 0.5 + gloss * 0.4;
}
