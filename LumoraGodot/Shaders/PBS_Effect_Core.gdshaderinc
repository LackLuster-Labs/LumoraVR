// Shared body of the PBS families with an effect on top: slice, stencil, displace and distance lerp.
//
// The surface underneath is the plain PBS one, in either workflow. Metallic writes METALLIC and ROUGHNESS
// and lets Godot's own PBR light it, the same arithmetic as PBS_Metallic_Core. PBS_SPECULAR switches to the
// coloured-F0 path of PBS_Specular_Core, light() and all. The family include declares its own uniforms and
// helpers, defines PBS_SLICE, PBS_DISPLACE or PBS_DISTANCE_LERP, and includes this last; the stencil family's
// generated source defines PBS_STENCIL and includes it directly. The blocks under those names are the whole
// of what each family adds.
//
// These families read occlusion from RED, where PBS_Metallic_Core reads green, and emission as the colour's RGB
// alone. Imported content was tuned against that. -xlinka

// The detail albedo is a 2x multiply in gamma space,
// which is 2^2.2 once the sampler has linearised the texel. Mid grey is the neutral detail colour.
const float DETAIL_COLOR_DOUBLE = 4.59479380;

// ---------------------------------------------------------------- albedo
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

#include "res://Shaders/TextureProfile.gdshaderinc"

// ---------------------------------------------------------------- workflow
#ifdef PBS_SPECULAR
// RGB is the reflectance at normal incidence, A is smoothness. The map replaces the colour outright.
uniform vec4 specular_color : source_color = vec4(0.0);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;
uniform int specular_map_profile = 0;
#include "res://Shaders/PBS_Specular_Light.gdshaderinc"
#else
// R is metallic, A is smoothness.
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map = false;
uniform int metallic_map_profile = 0;
#endif

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale = 1.0;

// ---------------------------------------------------------------- emission / occlusion
uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;

// ---------------------------------------------------------------- blending
uniform float alpha_clip : hint_range(0.0, 1.0) = 0.5;
// Set by the material hook from the BlendMode member; 1 is cutout. The rest is which variant is loaded.
uniform int blend_mode = 0;

vec3 effect_unpack_normal(vec4 packed_normal, float scale) {
	vec3 n = packed_normal.xyz * 2.0 - 1.0;
	n.xy *= scale;
	return n;
}

// The whiteout blend: add the slopes, multiply the heights.
vec3 effect_blend_normals(vec3 n1, vec3 n2) {
	return normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
}

void vertex() {
#ifdef PBS_SLICE
	slice_position = local_space ? VERTEX : (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
#endif
#ifdef PBS_DISPLACE
	VERTEX += NORMAL * displace_vertex_offset(UV, VERTEX, MODEL_MATRIX);
#endif
#ifdef PBS_DISTANCE_LERP
	vec3 reference = local_space ? VERTEX : (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec4 lerped = distance_lerp(reference);
	vec3 direction = dot(override_displacement_direction, override_displacement_direction) > 0.0
		? override_displacement_direction
		: NORMAL;
	VERTEX += direction * lerped.x;
	distance_emission = lerped.yzw;
#endif
}

void fragment() {
#ifdef PBS_STENCIL_NEVER
	// A comparison that never passes draws nothing and writes nothing.
	discard;
#endif

	vec2 base_uv = UV * texture_scale + texture_offset;
	vec4 tint = albedo_color;
	vec3 emission = emissive_color.rgb;

#ifdef PBS_SLICE
	float edge = slice_edge(slice_position);
	if (edge < 0.0) {
		discard;
	}
	tint = mix(albedo_color, edge_color, edge);
#endif

#ifdef PBS_DISPLACE
	base_uv += displace_uv_offset(UV);
#endif

	vec4 base = tint;
	if (use_albedo_texture) {
		base *= texture(albedo_texture, base_uv);
	}

#ifdef PBS_SLICE
	vec2 detail_uv = UV * detail_texture_scale + detail_texture_offset;
	if (use_detail_albedo_texture) {
		base.rgb *= texture(detail_albedo_texture, detail_uv).rgb * DETAIL_COLOR_DOUBLE;
	}
#endif

	if (blend_mode == 1 && base.a < alpha_clip) {
		discard;
	}

#ifdef PBS_SPECULAR
	vec4 spec = use_specular_map ? profile_color(texture(specular_map, base_uv), specular_map_profile) : specular_color;
	// Whatever the specular lobe reflects is not available to the diffuse one; the strongest channel sets
	// the budget.
	float reflectivity = max(max(spec.r, spec.g), spec.b);
	spec_f0 = spec.rgb;
	ALBEDO = base.rgb * (1.0 - reflectivity);
	ROUGHNESS = clamp(1.0 - spec.a, 0.0, 1.0);
	// Ambient and probes still run Godot's metallic path, which reads F0 as 0.16 * SPECULAR^2.
	METALLIC = 0.0;
	SPECULAR = clamp(sqrt(reflectivity / 0.16), 0.0, 1.0);
#else
	float metal = metallic;
	float gloss = smoothness;
	if (use_metallic_map) {
		vec4 mg = profile_data(texture(metallic_map, base_uv), metallic_map_profile);
		metal = mg.r;
		gloss = mg.a;
	}
	ALBEDO = base.rgb;
	METALLIC = clamp(metal, 0.0, 1.0);
	ROUGHNESS = clamp(1.0 - gloss, 0.0, 1.0);
#endif

	// Only the variants that define TRANSPARENT_PASS write ALPHA; the write alone puts a material in the
	// transparent pass. A stencil material is opaque whatever its albedo alpha says, and one that writes no
	// colour is fully see-through while it still writes depth and stencil.
#ifdef TRANSPARENT_PASS
#if defined(PBS_STENCIL_HIDDEN)
	ALPHA = 0.0;
#elif defined(PBS_STENCIL)
	ALPHA = 1.0;
#elif defined(PREMULTIPLIED)
	// A blended surface of these families is premultiplied: the diffuse fades with alpha, the reflections
	// keep their strength, and alpha grows by what the surface reflects.
#ifdef PBS_SPECULAR
	float premul_reflectivity = reflectivity;
#else
	float premul_reflectivity = mix(0.04, 1.0, clamp(metal, 0.0, 1.0));
#endif
	ALBEDO *= base.a;
	ALPHA = premul_reflectivity + base.a * (1.0 - premul_reflectivity);
#else
	ALPHA = base.a;
#endif
#endif

	if (use_emissive_map) {
		emission *= texture(emissive_map, base_uv).rgb;
	}
#ifdef PBS_SLICE
	emission = mix(emission, edge_emissive_color.rgb, edge);
#endif
#ifdef PBS_DISTANCE_LERP
	emission += distance_emission;
#endif
	EMISSION = emission;

	// Occlusion darkens only the light from the surroundings; direct lights are left alone.
	if (use_occlusion_map) {
		AO = profile_data(texture(occlusion_map, base_uv), occlusion_map_profile).r;
		AO_LIGHT_AFFECT = 0.0;
	}

#ifdef PBS_SLICE
	// The detail normal applies over a flat base when there is no normal map.
	if (use_normal_map || use_detail_normal_map) {
		vec3 tn = vec3(0.0, 0.0, 1.0);
		if (use_normal_map) {
			tn = effect_unpack_normal(texture(normal_map, base_uv), normal_scale);
		}
		if (use_detail_normal_map) {
			tn = effect_blend_normals(tn, effect_unpack_normal(texture(detail_normal_map, detail_uv), detail_normal_scale));
		}
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}
#else
	if (use_normal_map) {
		vec3 tn = effect_unpack_normal(texture(normal_map, base_uv), normal_scale);
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}
#endif
}
