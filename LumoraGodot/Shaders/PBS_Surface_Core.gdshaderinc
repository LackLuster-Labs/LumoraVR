// One-layer PBS surface for the generated families: PBS_TwoSided, PBS_RimLit, PBS_IntersectGlow and
// PBS_TriplanarSurface, in either workflow. Maps replace the values they stand for rather than scaling them,
// occlusion is read from red (green when projected), and emission is the colour's RGB times the map. The
// variant source picks the effect:
//
//   PBS_RIM                 view-angle rim added to the emission
//   PBS_INTERSECT           albedo and emission swing to their intersect colours where the surface sits just in
//                           front of whatever is behind it. Reads the depth texture, which files the variant in
//                           the blended pass on its own.
//   PBS_TRIPLANAR           every map projected on three planes from the world (or object) position, no UVs
//   PBS_VERTEX_ALBEDO       the mesh's vertex colour tints the albedo,
//   PBS_VERTEX_EMISSIVE     ... the emission,
//   PBS_VERTEX_REFLECTANCE  ... the reflectance: metallic by the colour's average and smoothness by its alpha,
//                           or the specular colour and smoothness channel for channel. -xlinka

#include "res://Shaders/PBS_Surface_Common.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale = 1.0;

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;

#ifdef PBS_SPECULAR
uniform vec4 specular_color : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;
uniform int specular_map_profile = 0;
#else
// Red is metallic, alpha is smoothness.
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map = false;
uniform int metallic_map_profile = 0;
#endif

#ifdef PBS_RIM
uniform vec4 rim_color : source_color = vec4(1.0, 0.0, 0.0, 1.0);
uniform float rim_power = 3.0;
#endif

#ifdef PBS_INTERSECT
uniform vec4 intersect_albedo_color : source_color = vec4(1.0);
uniform vec4 intersect_emissive_color : source_color = vec4(1.2, 0.8, 0.8, 1.0);
// Metres between this surface and what lies behind it. The glow ramps in across the begin pair and back out
// across the end pair; a pair with no width is a hard edge.
uniform float begin_transition_start = 0.0;
uniform float begin_transition_end = 0.0;
uniform float end_transition_start = 0.01;
uniform float end_transition_end = 0.01;
uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
#endif

#ifdef PBS_TRIPLANAR
// Exponent on the normal's components that weighs the three planes: low blurs them together, high cuts a
// seam. ObjectSpace projects from the mesh's own position, so the texture rides along when it moves.
uniform float triplanar_blend_power = 4.0;
uniform bool object_space = false;
varying vec3 tri_position;
varying vec3 tri_normal;

struct TriplanarFrame {
	vec2 uv_x;
	vec2 uv_y;
	vec2 uv_z;
	vec3 weight;
	vec3 axis_sign;
};

TriplanarFrame triplanar_frame(vec3 p, vec3 n) {
	TriplanarFrame t;
	t.weight = clamp(pow(abs(n), vec3(triplanar_blend_power)), 0.0, 1.0);
	t.weight /= max(t.weight.x + t.weight.y + t.weight.z, 0.0001);
	// The offset's second lane is the V scale, not the V offset. That is how imported triplanar surfaces
	// were laid out, so it stays, or every one of them would slide.
	vec2 shift = vec2(texture_offset.x, texture_scale.y);
	t.uv_x = p.zy * texture_scale + shift;
	t.uv_y = p.xz * texture_scale + shift;
	t.uv_z = p.xy * texture_scale + shift;
	// Mirrored on the far side of each axis so a texture never reads backwards on the back of a wall.
	t.axis_sign = vec3(n.x < 0.0 ? -1.0 : 1.0, n.y < 0.0 ? -1.0 : 1.0, n.z < 0.0 ? -1.0 : 1.0);
	t.uv_x.x *= t.axis_sign.x;
	t.uv_y.x *= t.axis_sign.y;
	t.uv_z.x *= -t.axis_sign.z;
	return t;
}

vec4 tri_tex(sampler2D tex, TriplanarFrame t) {
	return texture(tex, t.uv_x) * t.weight.x + texture(tex, t.uv_y) * t.weight.y + texture(tex, t.uv_z) * t.weight.z;
}

// Reoriented normal mapping: the plane's tangent normal bent onto the surface normal, so detail on the planes
// that face away is kept rather than averaged flat.
vec3 tri_reorient(vec3 base, vec3 detail) {
	base.z += 1.0;
	detail.xy = -detail.xy;
	return base * dot(base, detail) / base.z - detail;
}

#define SURFACE_TEX(tex) tri_tex(tex, tri)
#else
#define SURFACE_TEX(tex) texture(tex, base_uv)
#endif

#if defined(PBS_VERTEX_ALBEDO) || defined(PBS_VERTEX_EMISSIVE) || defined(PBS_VERTEX_REFLECTANCE)
#define PBS_VERTEX_COLOR
#include "res://Shaders/VertexColorSpace.gdshaderinc"
#endif

void vertex() {
#ifdef PBS_VERTEX_COLOR
	COLOR = vertex_color_encode(COLOR);
#endif
#ifdef PBS_TRIPLANAR
	tri_position = object_space ? VERTEX : (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	tri_normal = object_space ? NORMAL : MODEL_NORMAL_MATRIX * NORMAL;
#endif
}

void fragment() {
	vec2 base_uv = UV * texture_scale + texture_offset;
#ifdef PBS_TRIPLANAR
	vec3 tri_n = normalize(tri_normal);
	TriplanarFrame tri = triplanar_frame(tri_position, tri_n);
#endif
#ifdef PBS_VERTEX_COLOR
	vec4 tint = vertex_color_decode(COLOR);
#endif

#ifdef PBS_INTERSECT
	float raw_depth = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw_depth, 1.0);
	float gap = -(unprojected.z / unprojected.w) + VERTEX.z;
	float intersect = gap < end_transition_start
		? pbs_ramp(gap, begin_transition_start, begin_transition_end)
		: pbs_ramp(-gap, -end_transition_end, -end_transition_start);
	vec4 base = mix(albedo_color, intersect_albedo_color, intersect);
#else
	vec4 base = albedo_color;
#endif
	if (use_albedo_texture) {
		base *= SURFACE_TEX(albedo_texture);
	}
#ifdef PBS_VERTEX_ALBEDO
	base *= tint;
#endif

	vec3 pbs_albedo = base.rgb;
	float pbs_alpha = base.a;

	vec3 pbs_normal = vec3(0.0, 0.0, 1.0);
	bool pbs_use_normal = use_normal_map;
#ifdef PBS_TRIPLANAR
#define PBS_VIEW_NORMAL
	vec3 projected_normal = tri_n;
	if (use_normal_map) {
		vec3 nx = pbs_unpack_normal(texture(normal_map, tri.uv_x), normal_scale);
		vec3 ny = pbs_unpack_normal(texture(normal_map, tri.uv_y), normal_scale);
		vec3 nz = pbs_unpack_normal(texture(normal_map, tri.uv_z), normal_scale);
		nx.x *= tri.axis_sign.x;
		ny.x *= tri.axis_sign.y;
		nz.x *= -tri.axis_sign.z;
		vec3 a = abs(tri_n);
		nx = tri_reorient(vec3(tri_n.zy, a.x), nx);
		ny = tri_reorient(vec3(tri_n.xz, a.y), ny);
		nz = tri_reorient(vec3(tri_n.xy, a.z), nz);
		nx.z *= tri.axis_sign.x;
		ny.z *= tri.axis_sign.y;
		nz.z *= tri.axis_sign.z;
		projected_normal = normalize(nx.zyx * tri.weight.x + ny.xzy * tri.weight.y + nz.xyz * tri.weight.z);
	}
	mat3 to_view = object_space ? mat3(VIEW_MATRIX) * mat3(MODEL_MATRIX) : mat3(VIEW_MATRIX);
	vec3 pbs_view_normal = normalize(to_view * projected_normal);
	// A back face is lit as the side it is: mirrored across the face it belongs to.
	if (!FRONT_FACING) {
		pbs_view_normal = reflect(pbs_view_normal, NORMAL);
	}
#endif

	bool pbs_use_occlusion = use_occlusion_map;
#ifdef PBS_TRIPLANAR
	float pbs_occlusion = use_occlusion_map ? profile_data(SURFACE_TEX(occlusion_map), occlusion_map_profile).g : 1.0;
#else
	float pbs_occlusion = use_occlusion_map ? profile_data(SURFACE_TEX(occlusion_map), occlusion_map_profile).r : 1.0;
#endif

#ifdef PBS_SPECULAR
	vec4 pbs_specular = use_specular_map ? profile_color(SURFACE_TEX(specular_map), specular_map_profile) : specular_color;
#ifdef PBS_VERTEX_REFLECTANCE
	pbs_specular *= tint;
#endif
#else
	float pbs_metallic = metallic;
	float pbs_smoothness = smoothness;
	if (use_metallic_map) {
		vec4 mg = profile_data(SURFACE_TEX(metallic_map), metallic_map_profile);
		pbs_metallic = mg.r;
		pbs_smoothness = mg.a;
	}
#ifdef PBS_VERTEX_REFLECTANCE
	pbs_metallic *= (tint.r + tint.g + tint.b) / 3.0;
	pbs_smoothness *= tint.a;
#endif
#endif

	vec3 pbs_emission = emissive_color.rgb;
	if (use_emissive_map) {
		pbs_emission *= SURFACE_TEX(emissive_map).rgb;
	}
#ifdef PBS_VERTEX_EMISSIVE
	pbs_emission *= tint.rgb;
#endif
#ifdef PBS_INTERSECT
	pbs_emission += intersect_emissive_color.rgb * intersect;
#endif

#ifdef PBS_RIM
	// Against the normal the surface is finally lit with, map included.
#ifdef PBS_VIEW_NORMAL
	vec3 lit_normal = pbs_view_normal;
#else
	vec3 lit_normal = pbs_use_normal
		? normalize(TANGENT * pbs_normal.x + BINORMAL * pbs_normal.y + NORMAL * pbs_normal.z)
		: NORMAL;
#endif
	float rim = 1.0 - clamp(dot(normalize(VIEW), lit_normal), 0.0, 1.0);
	pbs_emission += rim_color.rgb * pow(max(rim, 1e-6), max(rim_power, 0.0));
#endif

#include "res://Shaders/PBS_Surface_Write.gdshaderinc"
}
