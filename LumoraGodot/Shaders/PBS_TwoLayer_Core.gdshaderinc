// Two complete PBS surfaces blended into one by a factor: a clean and a dirty version of the same wall, a
// texture fading into another. Each layer has its own albedo, emission, normal, occlusion and reflectance at
// its own tiling, and everything is mixed by the same amount.
//
// The amount is lerp_amount, or the lerp texture's red at its own tiling. multiply_values multiplies the
// texture by lerp_amount and scales each layer's metallic, smoothness or specular map by its values, where
// otherwise the map replaces them. Occlusion is read from red. -xlinka

#include "res://Shaders/PBS_Surface_Common.gdshaderinc"

uniform float lerp_amount : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D lerp_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_lerp_texture = false;
uniform bool multiply_values = true;
uniform vec2 lerp_texture_scale = vec2(1.0);
uniform vec2 lerp_texture_offset = vec2(0.0);

uniform vec2 texture0_scale = vec2(1.0);
uniform vec2 texture0_offset = vec2(0.0);
uniform vec2 texture1_scale = vec2(1.0);
uniform vec2 texture1_offset = vec2(0.0);

uniform vec4 albedo_color0 : source_color = vec4(1.0);
uniform vec4 albedo_color1 : source_color = vec4(1.0);
uniform sampler2D albedo_texture0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D albedo_texture1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture0 = false;
uniform bool use_albedo_texture1 = false;

uniform vec4 emissive_color0 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 emissive_color1 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emissive_map1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map0 = false;
uniform bool use_emissive_map1 = false;

uniform sampler2D normal_map0 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D normal_map1 : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map0 = false;
uniform bool use_normal_map1 = false;
uniform float normal_scale0 = 1.0;
uniform float normal_scale1 = 1.0;

uniform sampler2D occlusion_map0 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D occlusion_map1 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map0 = false;
uniform bool use_occlusion_map1 = false;
uniform int occlusion_map0_profile = 0;
uniform int occlusion_map1_profile = 0;

#ifdef PBS_SPECULAR
uniform vec4 specular_color0 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform vec4 specular_color1 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform sampler2D specular_map0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_map1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map0 = false;
uniform bool use_specular_map1 = false;
uniform int specular_map0_profile = 0;
uniform int specular_map1_profile = 0;
#else
uniform float metallic0 : hint_range(0.0, 1.0) = 0.0;
uniform float metallic1 : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness0 : hint_range(0.0, 1.0) = 0.25;
uniform float smoothness1 : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map0 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_map1 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map0 = false;
uniform bool use_metallic_map1 = false;
uniform int metallic_map0_profile = 0;
uniform int metallic_map1_profile = 0;
#endif

void fragment() {
	vec2 uv0 = UV * texture0_scale + texture0_offset;
	vec2 uv1 = UV * texture1_scale + texture1_offset;

	float l = lerp_amount;
	if (use_lerp_texture) {
		float mask = texture(lerp_texture, UV * lerp_texture_scale + lerp_texture_offset).r;
		l = multiply_values ? l * mask : mask;
	}

	vec4 c0 = albedo_color0;
	vec4 c1 = albedo_color1;
	if (use_albedo_texture0) {
		c0 *= texture(albedo_texture0, uv0);
	}
	if (use_albedo_texture1) {
		c1 *= texture(albedo_texture1, uv1);
	}
	vec4 base = mix(c0, c1, l);
	vec3 pbs_albedo = base.rgb;
	float pbs_alpha = base.a;

	bool pbs_use_normal = use_normal_map0 || use_normal_map1;
	vec3 n0 = use_normal_map0 ? pbs_unpack_normal(texture(normal_map0, uv0), normal_scale0) : vec3(0.0, 0.0, 1.0);
	vec3 n1 = use_normal_map1 ? pbs_unpack_normal(texture(normal_map1, uv1), normal_scale1) : vec3(0.0, 0.0, 1.0);
	vec3 pbs_normal = mix(n0, n1, l);

	bool pbs_use_occlusion = use_occlusion_map0 || use_occlusion_map1;
	float o0 = use_occlusion_map0 ? profile_data(texture(occlusion_map0, uv0), occlusion_map0_profile).r : 1.0;
	float o1 = use_occlusion_map1 ? profile_data(texture(occlusion_map1, uv1), occlusion_map1_profile).r : 1.0;
	float pbs_occlusion = mix(o0, o1, l);

	vec3 e0 = emissive_color0.rgb;
	vec3 e1 = emissive_color1.rgb;
	if (use_emissive_map0) {
		e0 *= texture(emissive_map0, uv0).rgb;
	}
	if (use_emissive_map1) {
		e1 *= texture(emissive_map1, uv1).rgb;
	}
	vec3 pbs_emission = mix(e0, e1, l);

#ifdef PBS_SPECULAR
	vec4 s0 = specular_color0;
	vec4 s1 = specular_color1;
	if (use_specular_map0) {
		vec4 st0 = profile_color(texture(specular_map0, uv0), specular_map0_profile);
		s0 = multiply_values ? s0 * st0 : st0;
	}
	if (use_specular_map1) {
		vec4 st1 = profile_color(texture(specular_map1, uv1), specular_map1_profile);
		s1 = multiply_values ? s1 * st1 : st1;
	}
	vec4 pbs_specular = mix(s0, s1, l);
#else
	vec2 m0 = vec2(metallic0, smoothness0);
	vec2 m1 = vec2(metallic1, smoothness1);
	if (use_metallic_map0) {
		vec2 mg = profile_data(texture(metallic_map0, uv0), metallic_map0_profile).ra;
		m0 = multiply_values ? m0 * mg : mg;
	}
	if (use_metallic_map1) {
		vec2 mg = profile_data(texture(metallic_map1, uv1), metallic_map1_profile).ra;
		m1 = multiply_values ? m1 * mg : mg;
	}
	vec2 m = mix(m0, m1, l);
	float pbs_metallic = m.x;
	float pbs_smoothness = m.y;
#endif

#include "res://Shaders/PBS_Surface_Write.gdshaderinc"
}
