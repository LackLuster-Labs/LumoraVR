shader_type spatial;
// Bends whatever is behind the surface along its view-space normal, optionally a normal-mapped one.
// The offset is divided by the fragment's depth, so the same glass bends less of the screen far away
// and a package's refraction strength reads the same here.
//
// A sample that lands on something IN FRONT of the glass would pull that thing into the refraction.
// The depth at the bent sample is checked against the glass: the bend fades back to none as the
// sampled surface comes nearer than depth_bias behind the glass, over depth_fade_divisor metres. A
// large bias with a tiny divisor bends through everything. -xlinka
render_mode unshaded, fog_disabled, blend_mix, cull_disabled, depth_draw_never;

#include "res://Shaders/ScreenFilter_Core.gdshaderinc"

uniform float refraction_strength = 0.05;
uniform float depth_bias = 0.0;
uniform float depth_fade_divisor = 0.1;

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform vec4 normal_map_transform = vec4(1.0, 1.0, 0.0, 0.0);

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;

float scene_distance(vec2 uv, mat4 inv_projection) {
	float raw = texture(scene_depth, uv).r;
	vec4 unprojected = inv_projection * vec4(uv * 2.0 - 1.0, raw, 1.0);
	return -(unprojected.z / unprojected.w);
}

void fragment() {
	if (filter_culled(FRONT_FACING)) {
		discard;
	}

	// Godot turns a back face's normal toward the eye; the authored bend uses the mesh normal as is.
	vec3 n = FRONT_FACING ? NORMAL : -NORMAL;
	if (use_normal_map) {
		// Tiling is applied v-up, as authored, then turned back to our top-origin rows.
		vec2 uv = vec2(UV.x, 1.0 - UV.y) * normal_map_transform.xy + normal_map_transform.zw;
		vec2 nxy = texture(normal_map, vec2(uv.x, 1.0 - uv.y)).rg * 2.0 - 1.0;
		vec3 tn = vec3(nxy, sqrt(max(1.0 - dot(nxy, nxy), 0.0)));
		n = normalize(TANGENT * tn.x + BINORMAL * tn.y + n * tn.z);
	}

	float depth = -VERTEX.z;
	vec2 bent = SCREEN_UV - n.xy / depth * refraction_strength;

	float fade = clamp((scene_distance(bent, INV_PROJECTION_MATRIX) - depth + depth_bias) / max(depth_fade_divisor, 0.000001), 0.0, 1.0);
	bent = mix(SCREEN_UV, bent, fade);

	vec3 behind = blend_mode >= 4 ? texture(screen_behind, SCREEN_UV).rgb : vec3(0.0);
	ALBEDO = filter_blend(texture(screen_behind, bent).rgb, behind);
	ALPHA = 1.0;
}
