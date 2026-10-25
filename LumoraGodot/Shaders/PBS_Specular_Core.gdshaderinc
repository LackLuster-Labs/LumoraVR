// Shared body of the specular-workflow PBS material.
//
// Ported from Unity's built-in Standard (Specular setup) shader, MIT licensed, Copyright (c) 2016
// Unity Technologies - UnityStandardCore.cginc / UnityStandardBRDF.cginc. This is a translation of
// that BRDF to Godot 4's spatial shader.
//
// The distinction from the metallic material is not cosmetic. In the metallic workflow the reflectance
// at normal incidence is derived: a dielectric gets a fixed grey 4% and a metal reflects its own albedo.
// In the specular workflow the author SUPPLIES that reflectance directly, as a colour, and the diffuse
// colour is whatever energy is left over. There is no metallic value to recover, so pushing a specular
// map through a metallic slot does not approximate the look, it inverts it: the map's blue channel gets
// read as "how metal is this", and a surface authored with a coloured specular tint comes out fully
// metallic and near-black in the diffuse.
//
// Godot's own PBR path has no coloured F0, so direct lighting runs through a light() of our own.
// Ambient and reflections still go through Godot, fed the closest metallic-workflow equivalent. -xlinka
//
// The detail layer and the parallax are the same pair the metallic material carries: a second
// albedo and normal at their own tiling over the base, and a height map that leans the UV by the view
// angle. A variant that defines DEPTH_OFFSET writes a biased depth, see DepthOffset.gdshaderinc. -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

// ---------------------------------------------------------------- albedo
uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);
uniform int albedo_uv : hint_enum("UV1", "UV2", "UV3", "UV4") = 0;
uniform int emissive_uv : hint_enum("UV1", "UV2", "UV3", "UV4") = 0;
#include "res://Shaders/MeshUV.gdshaderinc"
#include "res://Shaders/TextureProfile.gdshaderinc"

// ---------------------------------------------------------------- specular / gloss
// RGB is the reflectance colour at normal incidence, A is smoothness,
// and the same packing the specular COLOUR uses, which is why there is no separate smoothness value.
//
// The map REPLACES the colour rather than tinting it:
// when a map is set the sampled texel is the whole answer and the colour is not consulted.
// Multiplying instead would darken every mapped surface by whatever the flat colour happened to be.
uniform vec4 specular_color : source_color = vec4(0.5, 0.5, 0.5, 1.0);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;
uniform int specular_map_profile = 0;

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- emission / occlusion
uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;

// ---------------------------------------------------------------- detail
// The detail albedo is doubled so a neutral detail texture, mid grey, leaves the base alone.
// Doubled in GAMMA: the sample here is already linear, where mid grey is 0.214, so the factor is 2 raised
// to 2.2. A plain 2 would darken every detailed surface to
// 43 percent.
const float DETAIL_DOUBLE = 4.59479380;
uniform sampler2D detail_albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_detail_albedo_texture = false;
uniform sampler2D detail_normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_detail_normal_map = false;
uniform float detail_normal_scale : hint_range(0.0, 4.0) = 1.0;
uniform vec2 detail_texture_scale = vec2(1.0);
uniform vec2 detail_texture_offset = vec2(0.0);

// ---------------------------------------------------------------- parallax
// The parallax height is clamped to 0.005..0.08, a height in UV units; anything wider smears the surface into
// streaks rather than giving it depth, so it is clamped here too. Height is read from GREEN.
uniform sampler2D height_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_height_map = false;
uniform float height_scale : hint_range(0.005, 0.08) = 0.02;

uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
// Set by the material hook from the BlendMode member. 0 Opaque, 1 Cutout, 2 Alpha, 3 Transparent,
// 4 Additive, 5 Multiply. Only the cutout case changes anything here: blending itself is a compile-time
// render_mode, so the rest is handled by which variant the hook loaded.
uniform int blend_mode = 0;

// Direct lighting and the spec_f0 varying it reads.
#include "res://Shaders/PBS_Specular_Light.gdshaderinc"

// The map is tangent space; the scale leans only the XY.
vec3 unpack_scale_normal(vec4 packed_normal, float scale) {
	vec3 n = packed_normal.xyz * 2.0 - 1.0;
	n.xy *= scale;
	return n;
}

// The whiteout blend: add the slopes, multiply the heights.
vec3 blend_normals(vec3 n1, vec3 n2) {
	return normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
}

// One-step parallax offset. The +0.42 on z keeps the offset from exploding at grazing angles.
vec2 parallax_offset(float h, float height, vec3 view_dir) {
	h = h * height - height * 0.5;
	vec3 v = normalize(view_dir);
	v.z += 0.42;
	return h * (v.xy / v.z);
}

void vertex() {
	lm_uv34 = CUSTOM1;
}

void fragment() {
	// The base transform used to run per vertex. The detail layer needs the untransformed UV beside it,
	// and an affine map comes out the same whether it is applied per vertex or per pixel.
	vec2 base_uv = UV * texture_scale + texture_offset;
	vec2 detail_uv = UV * detail_texture_scale + detail_texture_offset;
	vec2 albedo_coords = mesh_uv(albedo_uv, UV, UV2, lm_uv34) * texture_scale + texture_offset;
	vec2 emissive_coords = mesh_uv(emissive_uv, UV, UV2, lm_uv34) * texture_scale + texture_offset;

	// Shifts BOTH uv sets, so the detail stays on the surface it details.
	if (use_height_map) {
		vec3 view_tangent = vec3(dot(VIEW, TANGENT), dot(VIEW, BINORMAL), dot(VIEW, NORMAL));
		vec2 offset = parallax_offset(texture(height_map, base_uv).g, clamp(height_scale, 0.005, 0.08), view_tangent);
		base_uv += offset;
		detail_uv += offset;
		albedo_coords += offset;
		emissive_coords += offset;
	}

	vec4 base = albedo_color;
	if (use_albedo_texture) {
		base *= texture(albedo_texture, albedo_coords);
	}
	if (use_detail_albedo_texture) {
		base.rgb *= texture(detail_albedo_texture, detail_uv).rgb * DETAIL_DOUBLE;
	}

	vec4 spec = specular_color;
	if (use_specular_map) {
		spec = profile_color(texture(specular_map, base_uv), specular_map_profile);
	}
	float gloss = spec.a;

	// Energy conservation: whatever the specular
	// lobe reflects is not available to the diffuse one, and the strongest channel sets that budget.
	float reflectivity = max(max(spec.r, spec.g), spec.b);

	spec_f0 = spec.rgb;

	// Only the cutout variants compile the discard: one anywhere in a shader costs it Godot's shared depth and
	// shadow material.
#ifdef ALPHA_CUTOUT
	if (blend_mode == 1 && base.a < alpha_cutoff) {
		discard;
	}
#endif

	ALBEDO = base.rgb * (1.0 - reflectivity);
	// Writing ALPHA at all files a material in the blended pass, so only the blended variants do. Transparent
	// is premultiplied: the diffuse fades with alpha while the reflections keep their strength. -xlinka
#if defined(TRANSPARENT_PASS) && defined(PREMULTIPLIED)
	ALBEDO *= base.a;
	ALPHA = reflectivity + base.a * (1.0 - reflectivity);
#elif defined(TRANSPARENT_PASS)
	ALPHA = base.a;
#endif
	ROUGHNESS = clamp(1.0 - gloss, 0.0, 1.0);

	// Ambient and reflection probes still run Godot's metallic path, which reconstructs F0 as
	// 0.16 * specular^2 for a dielectric. Solving that for the reflectivity we actually have keeps the
	// indirect term at the same brightness as the direct one instead of defaulting to a flat 4%.
	METALLIC = 0.0;
	SPECULAR = clamp(sqrt(reflectivity / 0.16), 0.0, 1.0);

	// The emission colour's alpha is not a strength.
	vec3 emission = emissive_color.rgb;
	if (use_emissive_map) {
		emission *= texture(emissive_map, emissive_coords).rgb;
	}
	EMISSION = emission;

	// Green, as the metallic material reads it, and only for the light from the surroundings; direct lights
	// are left alone.
	if (use_occlusion_map) {
		AO = profile_data(texture(occlusion_map, base_uv), occlusion_map_profile).g;
		AO_LIGHT_AFFECT = 0.0;
	}

	// A detail normal still applies with no base normal map, blended over a flat one.
	if (use_normal_map || use_detail_normal_map) {
		vec3 tn = vec3(0.0, 0.0, 1.0);
		if (use_normal_map) {
			tn = unpack_scale_normal(texture(normal_map, base_uv), normal_scale);
		}
		if (use_detail_normal_map) {
			tn = blend_normals(tn, unpack_scale_normal(texture(detail_normal_map, detail_uv), detail_normal_scale));
		}
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
