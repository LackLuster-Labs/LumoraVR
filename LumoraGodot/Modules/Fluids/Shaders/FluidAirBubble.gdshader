// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The reflective gas boundary enters the opaque scene before liquid transmission absorbs it.
// The clear centre leaves the scene intact; this boundary approximation does not refract it. -xlinka
shader_type spatial;
render_mode cull_back, depth_draw_opaque, diffuse_burley, specular_schlick_ggx;

void fragment()
{
    vec3 n = normalize(NORMAL);
    vec3 bent = refract(-VIEW, n, 1.45);
    float internal_reflection = 1.0 - smoothstep(0.0, 0.04, dot(bent, bent));
    if (internal_reflection < 0.5) discard;
    ALBEDO = vec3(0.012);
    EMISSION = vec3(0.0);
    NORMAL = n;
    ROUGHNESS = 0.025;
    METALLIC = 0.0;
    SPECULAR = 1.0;
    CLEARCOAT = 1.0;
    CLEARCOAT_ROUGHNESS = 0.01;
}
