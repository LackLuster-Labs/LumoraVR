// Four PBS layers painted across a surface by a splat map, terrain style: the map's RGBA says how much of
// layers 0 to 3 shows, normalised so the four always add up to one. All four layers share one tiling; the map
// has its own.
//
// A packed height map (one height per layer in RGBA) sharpens the blend: each layer's height is weighed by the
// map, and only the layers within height_transition_range of the tallest survive, so stones poke through sand
// instead of the two smearing into each other. Normals come packed two layers to a texture (xy and zw), the
// height rebuilt from the slope; metallic and smoothness likewise (metallic_map01 holds layers 0 and 1 as
// metal, smoothness pairs) and scale the values. A packed emission map holds one mask per layer, otherwise
// each layer has its own map. -xlinka

#include "res://Shaders/PBS_Surface_Common.gdshaderinc"

uniform sampler2D color_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_color_map = false;
uniform vec2 color_map_scale = vec2(1.0);
uniform vec2 color_map_offset = vec2(0.0);

uniform sampler2D packed_height_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_packed_height_map = false;
uniform float height_transition_range = 0.05;

uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

uniform vec4 albedo_color0 : source_color = vec4(1.0);
uniform vec4 albedo_color1 : source_color = vec4(1.0);
uniform vec4 albedo_color2 : source_color = vec4(1.0);
uniform vec4 albedo_color3 : source_color = vec4(1.0);
uniform sampler2D albedo_texture0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D albedo_texture1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D albedo_texture2 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D albedo_texture3 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture0 = false;
uniform bool use_albedo_texture1 = false;
uniform bool use_albedo_texture2 = false;
uniform bool use_albedo_texture3 = false;

uniform vec4 emissive_color0 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 emissive_color1 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 emissive_color2 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 emissive_color3 : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emissive_map1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emissive_map2 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D emissive_map3 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map0 = false;
uniform bool use_emissive_map1 = false;
uniform bool use_emissive_map2 = false;
uniform bool use_emissive_map3 = false;
uniform sampler2D packed_emission_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_packed_emission_map = false;

uniform sampler2D packed_normal_map01 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D packed_normal_map23 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_packed_normal_map01 = false;
uniform bool use_packed_normal_map23 = false;
uniform float normal_scale0 = 1.0;
uniform float normal_scale1 = 1.0;
uniform float normal_scale2 = 1.0;
uniform float normal_scale3 = 1.0;

#ifdef PBS_SPECULAR
uniform vec4 specular_color0 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform vec4 specular_color1 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform vec4 specular_color2 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform vec4 specular_color3 : source_color = vec4(1.0, 1.0, 1.0, 0.5);
uniform sampler2D specular_map0 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_map1 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_map2 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D specular_map3 : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map0 = false;
uniform bool use_specular_map1 = false;
uniform bool use_specular_map2 = false;
uniform bool use_specular_map3 = false;
uniform int specular_map0_profile = 0;
uniform int specular_map1_profile = 0;
uniform int specular_map2_profile = 0;
uniform int specular_map3_profile = 0;
#else
uniform vec4 metallic = vec4(0.0);
uniform vec4 smoothness = vec4(0.25);
uniform sampler2D metallic_map01 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D metallic_map23 : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map01 = false;
uniform bool use_metallic_map23 = false;
uniform int metallic_map01_profile = 0;
uniform int metallic_map23_profile = 0;
#endif

// A slope-only normal rebuilt to unit length.
vec3 splat_normal(vec2 packed_slope, float scale) {
	vec2 slope = (packed_slope * 2.0 - 1.0) * scale;
	return vec3(slope, sqrt(1.0 - clamp(dot(slope, slope), 0.0, 1.0)));
}

void fragment() {
	vec2 uv = UV * texture_scale + texture_offset;

	// With no map every layer shows equally.
	vec4 w = use_color_map ? texture(color_map, UV * color_map_scale + color_map_offset) : vec4(1.0);
	if (use_packed_height_map) {
		vec4 heights = texture(packed_height_map, uv) * w;
		float tallest = max(max(heights.x, heights.y), max(heights.z, heights.w));
		w = clamp((heights - tallest + height_transition_range) / max(height_transition_range, 1e-5), 0.0, 1.0);
	}
	w /= max(w.x + w.y + w.z + w.w, 1e-5);

	vec4 c0 = albedo_color0;
	vec4 c1 = albedo_color1;
	vec4 c2 = albedo_color2;
	vec4 c3 = albedo_color3;
	if (use_albedo_texture0) { c0 *= texture(albedo_texture0, uv); }
	if (use_albedo_texture1) { c1 *= texture(albedo_texture1, uv); }
	if (use_albedo_texture2) { c2 *= texture(albedo_texture2, uv); }
	if (use_albedo_texture3) { c3 *= texture(albedo_texture3, uv); }
	vec4 base = c0 * w.x + c1 * w.y + c2 * w.z + c3 * w.w;
	vec3 pbs_albedo = base.rgb;
	float pbs_alpha = base.a;

	vec3 e0 = emissive_color0.rgb;
	vec3 e1 = emissive_color1.rgb;
	vec3 e2 = emissive_color2.rgb;
	vec3 e3 = emissive_color3.rgb;
	if (use_packed_emission_map) {
		vec4 masks = texture(packed_emission_map, uv);
		e0 *= masks.x;
		e1 *= masks.y;
		e2 *= masks.z;
		e3 *= masks.w;
	} else {
		if (use_emissive_map0) { e0 *= texture(emissive_map0, uv).rgb; }
		if (use_emissive_map1) { e1 *= texture(emissive_map1, uv).rgb; }
		if (use_emissive_map2) { e2 *= texture(emissive_map2, uv).rgb; }
		if (use_emissive_map3) { e3 *= texture(emissive_map3, uv).rgb; }
	}
	vec3 pbs_emission = e0 * w.x + e1 * w.y + e2 * w.z + e3 * w.w;

	bool pbs_use_normal = use_packed_normal_map01 || use_packed_normal_map23;
	vec3 pbs_normal = vec3(0.0, 0.0, 1.0);
	if (pbs_use_normal) {
		vec4 n01 = use_packed_normal_map01 ? texture(packed_normal_map01, uv) : vec4(0.5);
		vec4 n23 = use_packed_normal_map23 ? texture(packed_normal_map23, uv) : vec4(0.5);
		pbs_normal = normalize(splat_normal(n01.xy, normal_scale0) * w.x + splat_normal(n01.zw, normal_scale1) * w.y
			+ splat_normal(n23.xy, normal_scale2) * w.z + splat_normal(n23.zw, normal_scale3) * w.w);
	}

	bool pbs_use_occlusion = false;
	float pbs_occlusion = 1.0;

#ifdef PBS_SPECULAR
	vec4 s0 = specular_color0;
	vec4 s1 = specular_color1;
	vec4 s2 = specular_color2;
	vec4 s3 = specular_color3;
	if (use_specular_map0) { s0 *= profile_color(texture(specular_map0, uv), specular_map0_profile); }
	if (use_specular_map1) { s1 *= profile_color(texture(specular_map1, uv), specular_map1_profile); }
	if (use_specular_map2) { s2 *= profile_color(texture(specular_map2, uv), specular_map2_profile); }
	if (use_specular_map3) { s3 *= profile_color(texture(specular_map3, uv), specular_map3_profile); }
	vec4 pbs_specular = s0 * w.x + s1 * w.y + s2 * w.z + s3 * w.w;
#else
	vec4 metal = metallic;
	vec4 gloss = smoothness;
	if (use_metallic_map01) {
		vec4 m01 = profile_data(texture(metallic_map01, uv), metallic_map01_profile);
		metal.xy *= m01.xz;
		gloss.xy *= m01.yw;
	}
	if (use_metallic_map23) {
		vec4 m23 = profile_data(texture(metallic_map23, uv), metallic_map23_profile);
		metal.zw *= m23.xz;
		gloss.zw *= m23.yw;
	}
	float pbs_metallic = dot(metal, w);
	float pbs_smoothness = dot(gloss, w);
#endif

#include "res://Shaders/PBS_Surface_Write.gdshaderinc"
}
