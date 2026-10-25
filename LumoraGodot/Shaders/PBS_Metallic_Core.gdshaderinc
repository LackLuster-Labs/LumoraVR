// Shared body of the metallic-workflow PBS material.
//
// Ported from Unity's built-in Standard (Metallic setup) shader, MIT licensed, Copyright (c) 2016
// Unity Technologies - UnityStandardCore.cginc / UnityStandardInput.cginc.
//
// Unlike the specular material next door, this one needs no light() of its own. The metallic workflow
// derives F0 rather than being handed it - a dielectric gets a fixed 4%, a metal reflects its own
// albedo - and that is exactly what Godot's built-in PBR does, off the same GGX distribution and Smith
// visibility term. Writing METALLIC and ROUGHNESS and letting the engine light it is not an
// approximation here, it is the same arithmetic. The specular material only owns its lighting because
// a COLOURED F0 has nowhere to live in Godot's pipeline.
//
// WHAT THIS DELIBERATELY DOES NOT EXPOSE: a smoothness scale, a choice of smoothness channel, an occlusion
// strength, a detail mask and a detail UV set. The material component drives none of them, so they are not
// uniforms here, they are fixed in the arithmetic: gloss scale 1, smoothness
// from the metallic map's ALPHA, occlusion at full strength, no detail mask, detail on UV0. Adding knobs
// nothing can turn would invent a material we do not have. -xlinka

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

// ---------------------------------------------------------------- metallic / gloss
// The metallic map is R = metallic, A = smoothness. NOT the glTF packing, where roughness sits in
// green: reading this one that way puts smoothness in an empty channel and every mapped surface comes
// out uniformly glossy.
//
// The slider could be read as gamma encoded, sRGB-decoded before use. We keep it linear,
// because that is what this material has always fed Godot and changing it would silently reshade every
// metallic surface in the project.
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map = false;
uniform int metallic_map_profile = 0;
// Scales the map by the sliders instead of letting it replace them.
uniform bool sliders_scale_map = false;

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- parallax
// The parallax height runs 0.005 to 0.08 and the clamp matters. This is a MODEL-SCALE height in UV
// space; an author who measured it in some other unit hands us a number that smears the surface into
// streaks rather than giving it depth. Clamped rather than trusted, the same lesson the toon outline
// width taught.
uniform sampler2D height_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_height_map = false;
uniform float height_scale : hint_range(0.005, 0.08) = 0.02;

// ---------------------------------------------------------------- occlusion / emission
// Occlusion is read from the GREEN channel, not red. Most occlusion maps are greyscale so it rarely
// shows, but a packed map where the channels differ is read wrong by anything that assumes red.
uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;

uniform vec4 emissive_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emissive_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

// ---------------------------------------------------------------- detail
// The detail albedo is a 2x multiply in gamma space, so a neutral detail texture is mid GREY,
// not white. The texels arrive linear here, where that grey is 0.2159, so the factor is 4.5948, not 2.
uniform sampler2D detail_albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_detail_albedo_texture = false;
uniform sampler2D detail_normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_detail_normal_map = false;
uniform float detail_normal_scale : hint_range(0.0, 4.0) = 1.0;
uniform vec2 detail_texture_scale = vec2(1.0);
uniform vec2 detail_texture_offset = vec2(0.0);

// ---------------------------------------------------------------- blending
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
// Set by the material hook from the BlendMode member. 0 Opaque, 1 Cutout, 2 Alpha, 3 Transparent,
// 4 Additive, 5 Multiply. Only cutout changes anything here; the rest is a compile-time render_mode and
// is handled by which variant the hook loaded.
uniform int blend_mode = 0;

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

// One-step parallax offset. The +0.42 on z is not a rounding of anything: it stops the
// offset exploding at grazing angles, where dividing by a near-zero z would throw the sample across the
// texture.
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
	vec2 base_uv = UV * texture_scale + texture_offset;
	vec2 detail_uv = UV * detail_texture_scale + detail_texture_offset;
	vec2 albedo_coords = mesh_uv(albedo_uv, UV, UV2, lm_uv34) * texture_scale + texture_offset;
	vec2 emissive_coords = mesh_uv(emissive_uv, UV, UV2, lm_uv34) * texture_scale + texture_offset;

	// Parallax shifts BOTH uv sets together. Offsetting only the base would slide the detail layer off the surface it details.
	if (use_height_map) {
		vec3 view_tangent = vec3(dot(VIEW, TANGENT), dot(VIEW, BINORMAL), dot(VIEW, NORMAL));
		vec2 offset = parallax_offset(
			texture(height_map, base_uv).g,
			clamp(height_scale, 0.005, 0.08),
			view_tangent);
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
		base.rgb *= texture(detail_albedo_texture, detail_uv).rgb * 4.5947938;
	}

	// Only the cutout files compile the discard: one anywhere in a shader, taken or not, keeps Godot from
	// drawing its depth and shadow passes with the shared depth material. -xlinka
#ifdef ALPHA_CUTOUT
	if (blend_mode == 1 && base.a < alpha_cutoff) {
		discard;
	}
#endif

	float metal = metallic;
	float gloss = smoothness;
	if (use_metallic_map) {
		vec4 mg = profile_data(texture(metallic_map, base_uv), metallic_map_profile);
		metal = sliders_scale_map ? mg.r * metallic : mg.r;
		gloss = sliders_scale_map ? mg.a * smoothness : mg.a;
	}

	ALBEDO = base.rgb;
	// WRITING ALPHA AT ALL IS WHAT MAKES A MATERIAL TRANSPARENT IN GODOT, whatever value you write.
	// Not the blend mode, not the value being 1.0 - the mere presence of the write. A material in the
	// transparent pass writes no depth and is sorted per object against every other transparent thing,
	// so as the camera moves the order flips and surfaces pop in and out. Every opaque material here
	// was landing in that pass because this line ran unconditionally.
	//
	// The opaque variant does not define TRANSPARENT_PASS, so this whole write vanishes at preprocess
	// and Godot keeps the material in the opaque pass with proper depth. Same split the VertexColor
	// pair has always used. -xlinka
#if defined(TRANSPARENT_PASS) && defined(PREMULTIPLIED)
	// Transparent is premultiplied, glass rather than a fade: only the diffuse is scaled by alpha, so the
	// reflections stay at full strength, and alpha grows by what the surface reflects. -xlinka
	float reflectivity = mix(0.04, 1.0, clamp(metal, 0.0, 1.0));
	ALBEDO = base.rgb * base.a;
	ALPHA = reflectivity + base.a * (1.0 - reflectivity);
#elif defined(TRANSPARENT_PASS)
	ALPHA = base.a;
#endif
	METALLIC = clamp(metal, 0.0, 1.0);
	ROUGHNESS = clamp(1.0 - gloss, 0.0, 1.0);

	// The emission colour's alpha is not a strength. Content authored with a half-transparent emission
	// colour lit at half strength while it was read as one.
	vec3 emission = emissive_color.rgb;
	if (use_emissive_map) {
		emission *= texture(emissive_map, emissive_coords).rgb;
	}
	EMISSION = emission;

	// Occlusion darkens only the light that arrives from the surroundings, ambient and reflections. Direct
	// lights are left alone: a cavity map is not a shadow.
	if (use_occlusion_map) {
		AO = profile_data(texture(occlusion_map, base_uv), occlusion_map_profile).g;
		AO_LIGHT_AFFECT = 0.0;
	}

	// The detail normal still applies when there is no base normal map: it is blended against a flat
	// tangent normal in that case rather than skipping the detail.
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
}
