// PBS whose maps each pick their own UV set and tiling: a tiling albedo on the first set over a baked
// occlusion or glow on the second. A secondary albedo multiplies the first, a secondary
// emission map adds its own tinted glow on top. UV sets 1 to 4; 3 and 4 come in through CUSTOM1 (MeshUV).
// Maps replace the values they stand for; occlusion is read from red. -xlinka

#include "res://Shaders/PBS_Surface_Common.gdshaderinc"
#include "res://Shaders/MeshUV.gdshaderinc"

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 albedo_scale = vec2(1.0);
uniform vec2 albedo_offset = vec2(0.0);
uniform int albedo_uv = 0;

uniform sampler2D secondary_albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_secondary_albedo_texture = false;
uniform vec2 secondary_albedo_scale = vec2(1.0);
uniform vec2 secondary_albedo_offset = vec2(0.0);
uniform int secondary_albedo_uv = 0;

uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;
uniform vec2 emission_map_scale = vec2(1.0);
uniform vec2 emission_map_offset = vec2(0.0);
uniform int emission_map_uv = 0;

uniform vec4 secondary_emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D secondary_emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_secondary_emissive_map = false;
uniform vec2 secondary_emission_map_scale = vec2(1.0);
uniform vec2 secondary_emission_map_offset = vec2(0.0);
uniform int secondary_emission_map_uv = 0;

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale = 1.0;
uniform vec2 normal_map_scale = vec2(1.0);
uniform vec2 normal_map_offset = vec2(0.0);
uniform int normal_map_uv = 0;

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;
uniform vec2 occlusion_map_scale = vec2(1.0);
uniform vec2 occlusion_map_offset = vec2(0.0);
uniform int occlusion_map_uv = 0;

#ifdef PBS_SPECULAR
uniform vec4 specular_color : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;
uniform int specular_map_profile = 0;
uniform vec2 specular_map_scale = vec2(1.0);
uniform vec2 specular_map_offset = vec2(0.0);
uniform int specular_map_uv = 0;
#else
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map = false;
uniform int metallic_map_profile = 0;
uniform vec2 metallic_map_scale = vec2(1.0);
uniform vec2 metallic_map_offset = vec2(0.0);
uniform int metallic_map_uv = 0;
#endif

void vertex() {
	lm_uv34 = CUSTOM1;
}

void fragment() {
	vec4 base = albedo_color;
	if (use_albedo_texture) {
		base *= texture(albedo_texture, mesh_uv(albedo_uv, UV, UV2, lm_uv34) * albedo_scale + albedo_offset);
	}
	if (use_secondary_albedo_texture) {
		base *= texture(secondary_albedo_texture,
			mesh_uv(secondary_albedo_uv, UV, UV2, lm_uv34) * secondary_albedo_scale + secondary_albedo_offset);
	}
	vec3 pbs_albedo = base.rgb;
	float pbs_alpha = base.a;

	bool pbs_use_normal = use_normal_map;
	vec3 pbs_normal = vec3(0.0, 0.0, 1.0);
	if (use_normal_map) {
		pbs_normal = pbs_unpack_normal(texture(normal_map,
			mesh_uv(normal_map_uv, UV, UV2, lm_uv34) * normal_map_scale + normal_map_offset), normal_scale);
	}

	bool pbs_use_occlusion = use_occlusion_map;
	float pbs_occlusion = 1.0;
	if (use_occlusion_map) {
		pbs_occlusion = profile_data(texture(occlusion_map,
			mesh_uv(occlusion_map_uv, UV, UV2, lm_uv34) * occlusion_map_scale + occlusion_map_offset), occlusion_map_profile).r;
	}

	vec3 pbs_emission = emissive_color.rgb;
	if (use_emissive_map) {
		pbs_emission *= texture(emissive_map,
			mesh_uv(emission_map_uv, UV, UV2, lm_uv34) * emission_map_scale + emission_map_offset).rgb;
	}
	if (use_secondary_emissive_map) {
		pbs_emission += secondary_emissive_color.rgb * texture(secondary_emissive_map,
			mesh_uv(secondary_emission_map_uv, UV, UV2, lm_uv34) * secondary_emission_map_scale + secondary_emission_map_offset).rgb;
	}

#ifdef PBS_SPECULAR
	vec4 pbs_specular = specular_color;
	if (use_specular_map) {
		pbs_specular = profile_color(texture(specular_map,
			mesh_uv(specular_map_uv, UV, UV2, lm_uv34) * specular_map_scale + specular_map_offset), specular_map_profile);
	}
#else
	float pbs_metallic = metallic;
	float pbs_smoothness = smoothness;
	if (use_metallic_map) {
		vec4 mg = profile_data(texture(metallic_map, mesh_uv(metallic_map_uv, UV, UV2, lm_uv34) * metallic_map_scale + metallic_map_offset), metallic_map_profile);
		pbs_metallic = mg.r;
		pbs_smoothness = mg.a;
	}
#endif

#include "res://Shaders/PBS_Surface_Write.gdshaderinc"
}
