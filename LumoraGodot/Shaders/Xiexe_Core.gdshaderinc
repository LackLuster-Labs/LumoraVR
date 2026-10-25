// Shared body of the Xiexe toon material.
//
// Ported from Xiexe's Unity Shaders (XSToon), MIT licensed, Copyright (c) 2019 Xiexe.
// The lighting model is theirs; this is a translation of it to Godot 4's spatial shader, not a
// reimplementation from scratch - the ramp remap, the two rim terms, the subsurface wrap and the
// specular sharpening all follow XSLightingFunctions.cginc so an avatar authored against it lands
// looking the way its author intended.
//
// Kept entirely separate from the plain toon material. That one is our own simpler model and several
// worlds already use it; this is a second, denser model that happens to share a silhouette. Culling is
// a compile-time render_mode in Godot 4, so this ships as thin variant shaders that differ only in that
// line and pull the body in from here. -xlinka
//
// A depth write other than the prepass, or a depth bias, comes as generated source over this same body
// (XiexeToonMaterial), with DEPTH_OFFSET defined for the bias. -xlinka

#include "res://Shaders/MeshUV.gdshaderinc"
#include "res://Shaders/VertexColorSpace.gdshaderinc"
#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif
#include "res://Shaders/TextureProfile.gdshaderinc"

// ---------------------------------------------------------------- albedo
uniform vec4 base_color : source_color = vec4(1.0);
uniform sampler2D main_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_main_texture = false;
uniform vec2 main_texture_scale = vec2(1.0);
uniform vec2 main_texture_offset = vec2(0.0);
uniform bool use_vertex_colors = false;

// ---------------------------------------------------------------- map coordinates
// Each map reads its own UV set, 0 to 3. With maps_follow_albedo every map tiles with the albedo, which is all
// a map could do before per-map transforms existed; off, each map keeps its own scale and offset.
uniform bool maps_follow_albedo = true;
uniform int albedo_uv_set = 0;
uniform int normal_uv_set = 0;
uniform int metallic_uv_set = 0;
uniform int occlusion_uv_set = 0;
uniform int emission_uv_set = 0;
uniform int thickness_uv_set = 0;
uniform vec2 normal_map_scale = vec2(1.0);
uniform vec2 normal_map_offset = vec2(0.0);
uniform vec2 metallic_gloss_map_scale = vec2(1.0);
uniform vec2 metallic_gloss_map_offset = vec2(0.0);
uniform vec2 occlusion_map_scale = vec2(1.0);
uniform vec2 occlusion_map_offset = vec2(0.0);
uniform vec2 emission_map_scale = vec2(1.0);
uniform vec2 emission_map_offset = vec2(0.0);
uniform vec2 thickness_map_scale = vec2(1.0);
uniform vec2 thickness_map_offset = vec2(0.0);
// Turns the albedo's hue round the colour wheel, in whole turns.
uniform float hue_shift : hint_range(0.0, 1.0) = 0.0;
// Pulls the albedo toward or away from its own luminance before anything is lit.
uniform float saturation : hint_range(-1.0, 1.0) = 0.0;

// ---------------------------------------------------------------- normal
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

// ---------------------------------------------------------------- shading ramp
// The ramp IS the light response. U is the wrapped N.L, so the texture decides where the terminator
// sits and what colour the dark side takes; V picks a row, which lets one texture carry several ramps.
uniform sampler2D shadow_ramp : source_color, filter_linear, repeat_disable;
uniform bool use_shadow_ramp = false;
uniform float ramp_row : hint_range(0.0, 1.0) = 0.0;
// Picks the row per pixel instead: red is the height up the ramp, counted from its bottom row the way the
// mask was painted, so 0 reads the bottom row and 1 the top.
uniform sampler2D shadow_ramp_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_shadow_ramp_mask = false;
uniform vec2 shadow_ramp_mask_scale = vec2(1.0);
uniform vec2 shadow_ramp_mask_offset = vec2(0.0);
// Fallback when no ramp is bound: a hard-ish step with a soft edge.
uniform float shadow_sharpness : hint_range(0.0, 1.0) = 0.5;

// ---------------------------------------------------------------- rim (lit side)
uniform vec4 rim_color : source_color = vec4(1.0);
uniform float rim_intensity : hint_range(0.0, 4.0) = 0.0;
uniform float rim_range : hint_range(0.0, 1.0) = 0.7;
uniform float rim_threshold : hint_range(0.0, 1.0) = 0.1;
uniform float rim_sharpness : hint_range(0.0, 1.0) = 0.1;
uniform float rim_albedo_tint : hint_range(0.0, 1.0) = 0.0;
uniform float rim_attenuation_effect : hint_range(0.0, 1.0) = 0.0;

// ---------------------------------------------------------------- rim (shadow side)
uniform vec4 shadow_rim_color : source_color = vec4(1.0);
uniform float shadow_rim_range : hint_range(0.0, 1.0) = 0.0;
uniform float shadow_rim_threshold : hint_range(0.0, 1.0) = 0.1;
uniform float shadow_rim_sharpness : hint_range(0.0, 1.0) = 0.1;
uniform float shadow_rim_albedo_tint : hint_range(0.0, 1.0) = 0.0;

// ---------------------------------------------------------------- specular
uniform float specular_intensity : hint_range(0.0, 4.0) = 0.0;
// 0 is a mirror-hard toon dot, 1 lets the lobe spread into a normal highlight.
uniform float specular_area : hint_range(0.0, 1.0) = 0.5;
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float glossiness : hint_range(0.0, 1.0) = 0.5;
uniform float reflectivity : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D metallic_gloss_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_gloss_map = false;
uniform int metallic_gloss_map_profile = 0;

// ---------------------------------------------------------------- matcap / emission / occlusion
uniform sampler2D matcap : source_color, filter_linear_mipmap, repeat_disable;
uniform bool use_matcap = false;
uniform vec4 matcap_tint : source_color = vec4(1.0);
uniform vec2 matcap_scale = vec2(1.0);
uniform vec2 matcap_offset = vec2(0.0);

uniform sampler2D emission_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emission_map = false;
uniform vec4 emission_color : source_color = vec4(0.0);

uniform sampler2D occlusion_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;
uniform vec4 occlusion_color : source_color = vec4(1.0);

// ---------------------------------------------------------------- subsurface
// Light bleeding through thin geometry - ears, fins, membranes. Skipped entirely when the colour is
// black, exactly as the original does, because it is not free.
uniform sampler2D thickness_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_thickness_map = false;
uniform vec4 subsurface_color : source_color = vec4(0.0);
uniform float subsurface_distortion : hint_range(0.0, 1.0) = 0.2;
uniform float subsurface_power : hint_range(0.0, 16.0) = 1.0;
uniform float subsurface_scale : hint_range(0.0, 8.0) = 1.0;

// 0 opaque, 1 cutout, 2 blend.
uniform int alpha_mode : hint_range(0, 2) = 0;
uniform float alpha_clip : hint_range(0.0, 1.0) = 0.5;

varying vec3 vertex_color;
// Read per pixel here and handed to light(), which has no UV sets of its own.
varying float lit_thickness;
varying float lit_ramp_row;

vec2 map_coords(int set, vec2 scale, vec2 offset, vec2 uv0, vec2 uv1, vec4 uv34) {
	vec2 uv = mesh_uv(set, uv0, uv1, uv34);
	return maps_follow_albedo ? uv * main_texture_scale + main_texture_offset : uv * scale + offset;
}

void vertex() {
	lm_uv34 = CUSTOM1;
	vertex_color = vertex_color_encode(COLOR).rgb;
}

void fragment() {
	vec4 base = base_color;
	if (use_main_texture) {
		base *= texture(main_texture, mesh_uv(albedo_uv_set, UV, UV2, lm_uv34) * main_texture_scale + main_texture_offset);
	}
	if (use_vertex_colors) {
		base.rgb *= vertex_color_decode(vec4(vertex_color, 1.0)).rgb;
	}

	// Hue as a turn about the grey axis: red goes to yellow, then green, as the shift grows. Greys stay
	// put and brightness barely moves, which is what a hue slider is expected to do.
	float hue_turn = fract(hue_shift);
	if (hue_turn > 0.0005 && hue_turn < 0.9995) {
		float angle = hue_turn * 6.28318530718;
		vec3 axis = vec3(0.57735026919);
		float ca = cos(angle);
		base.rgb = max(base.rgb * ca + cross(axis, base.rgb) * sin(angle) + axis * dot(axis, base.rgb) * (1.0 - ca), vec3(0.0));
	}

	// Saturation around the surface's own luminance, before lighting - the original does this to the
	// albedo, so a desaturated ramp still reads against the original hue.
	if (abs(saturation) > 0.001) {
		float luma = dot(base.rgb, vec3(0.2126, 0.7152, 0.0722));
		base.rgb = clamp(mix(vec3(luma), base.rgb, 1.0 + saturation), 0.0, 8.0);
	}

	if (alpha_mode == 1 && base.a < alpha_clip) {
		discard;
	}

	// NORMAL, TANGENT and BINORMAL are view-space here, which is what both the matcap lookup and the
	// rim fresnel want, so nothing needs transforming back.
	if (use_normal_map) {
		vec2 at = map_coords(normal_uv_set, normal_map_scale, normal_map_offset, UV, UV2, lm_uv34);
		vec3 tn = texture(normal_map, at).xyz * 2.0 - 1.0;
		tn.xy *= normal_scale;
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}

	float occlusion = 1.0;
	if (use_occlusion_map) {
		vec2 at = map_coords(occlusion_uv_set, occlusion_map_scale, occlusion_map_offset, UV, UV2, lm_uv34);
		occlusion = profile_data(texture(occlusion_map, at), occlusion_map_profile).r;
		base.rgb *= mix(occlusion_color.rgb, vec3(1.0), occlusion);
	}

	vec3 emission = emission_color.rgb;
	if (use_emission_map) {
		vec2 at = map_coords(emission_uv_set, emission_map_scale, emission_map_offset, UV, UV2, lm_uv34);
		emission *= texture(emission_map, at).rgb;
	}

	// Matcap is a view-space normal lookup, added on top so it survives the shadow side.
	if (use_matcap) {
		vec3 cap = texture(matcap, (NORMAL.xy * 0.5 + 0.5) * matcap_scale + matcap_offset).rgb;
		emission += cap * matcap_tint.rgb * matcap_tint.a;
	}

	float m = metallic;
	float g = glossiness;
	if (use_metallic_gloss_map) {
		vec2 at = map_coords(metallic_uv_set, metallic_gloss_map_scale, metallic_gloss_map_offset, UV, UV2, lm_uv34);
		vec4 packed = profile_data(texture(metallic_gloss_map, at), metallic_gloss_map_profile);
		// Metallic in R, smoothness in A: not glTF's packing.
		m = packed.r;
		g = packed.a;
	}

	lit_thickness = 1.0;
	if (use_thickness_map && dot(subsurface_color.rgb, vec3(1.0)) > 0.0) {
		vec2 at = map_coords(thickness_uv_set, thickness_map_scale, thickness_map_offset, UV, UV2, lm_uv34);
		lit_thickness = texture(thickness_map, at).r;
	}

	// Our rows run down from the top of the texture, so a height up the ramp is read from the far end.
	lit_ramp_row = ramp_row;
	if (use_shadow_ramp_mask) {
		vec2 at = map_coords(0, shadow_ramp_mask_scale, shadow_ramp_mask_offset, UV, UV2, lm_uv34);
		lit_ramp_row = 1.0 - texture(shadow_ramp_mask, at).r;
	}

	ALBEDO = base.rgb;
	ALPHA = alpha_mode == 2 ? base.a : 1.0;
	EMISSION = emission;
	METALLIC = m;
	ROUGHNESS = clamp(1.0 - g, 0.0, 1.0);
	SPECULAR = reflectivity;
	AO = occlusion;

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}

void light() {
	vec3 n = normalize(NORMAL);
	vec3 l = normalize(LIGHT);
	vec3 v = normalize(VIEW);

	float ndl = dot(n, l);
	// Wrapped N.L: the ramp needs the whole sphere mapped into 0..1, not just the lit hemisphere,
	// or the dark side has no range left for the ramp to say anything about.
	float wrapped = ndl * 0.5 + 0.5;

	vec3 shade;
	if (use_shadow_ramp) {
		shade = texture(shadow_ramp, vec2(clamp(wrapped * ATTENUATION, 0.0, 1.0), lit_ramp_row)).rgb;
	} else {
		float edge = mix(0.35, 0.005, shadow_sharpness);
		shade = vec3(smoothstep(0.5 - edge, 0.5 + edge, wrapped));
	}

	// One minus the saturated view-dot-normal: the silhouette-facing rim factor both rim terms use.
	float svdn = 1.0 - clamp(dot(n, v), 0.0, 1.0);

	// LIT-SIDE RIM. Gated on N.L so it only appears where the light actually reaches, which is what
	// separates it from a plain fresnel.
	vec3 rim = vec3(0.0);
	if (rim_intensity > 0.0) {
		float ri = svdn * pow(clamp(ndl, 0.0, 1.0), max(rim_threshold, 0.001) * 8.0);
		ri = smoothstep(rim_range - rim_sharpness, rim_range + rim_sharpness, ri);
		rim = ri * rim_intensity * rim_color.rgb * mix(vec3(1.0), ALBEDO, rim_albedo_tint);
		rim *= mix(1.0, ATTENUATION, rim_attenuation_effect);
	}

	// SHADOW-SIDE RIM. The mirror of the above - it rides the DARK side and tints it rather than
	// adding light, so it reads as bounce rather than a second lamp.
	vec3 shadow_rim = vec3(1.0);
	if (shadow_rim_range > 0.0) {
		float sri = svdn * pow(clamp(1.0 - ndl, 0.0, 1.0), max(shadow_rim_threshold, 0.001) * 2.0);
		sri = smoothstep(shadow_rim_range - shadow_rim_sharpness, shadow_rim_range + shadow_rim_sharpness, sri);
		shadow_rim = mix(vec3(1.0), shadow_rim_color.rgb * mix(vec3(1.0), ALBEDO, shadow_rim_albedo_tint), sri);
	}

	// SUBSURFACE. Light arriving from behind, wrapped around the surface by distortion and focused by
	// power. Skipped when the colour is black - the original branches here for the same reason.
	vec3 sss = vec3(0.0);
	if (dot(subsurface_color.rgb, vec3(1.0)) > 0.0) {
		vec3 h = normalize(l + n * subsurface_distortion);
		float vdh = pow(clamp(dot(v, -h), 0.0, 1.0), max(subsurface_power, 0.001));
		sss = subsurface_color.rgb * vdh * lit_thickness * subsurface_scale
		    * clamp(ATTENUATION * wrapped, 0.0, 1.0) * ALBEDO;
	}

	// ATTENUATION scales the whole term so a shadowed or out-of-range light adds nothing at all.
	//
	// No ALBEDO multiply and a divide by PI, both deliberate: Godot multiplies DIFFUSE_LIGHT by ALBEDO
	// after this returns and hands LIGHT_COLOR in pre-scaled by PI, so writing ALBEDO here squares it
	// and skipping the divide comes out PI times hot. Same convention as the plain toon material.
	DIFFUSE_LIGHT += LIGHT_COLOR * shade * shadow_rim * ATTENUATION * (1.0 - METALLIC) / PI;
	DIFFUSE_LIGHT += LIGHT_COLOR * (rim + sss) / PI;

	// SPECULAR. A GGX lobe sharpened toward a hard toon dot by specular_area, which is what gives the
	// family its flat highlight instead of a photographic one.
	if (specular_intensity > 0.0) {
		float rough = max(ROUGHNESS * ROUGHNESS, 0.0045);
		vec3 h = normalize(l + v);
		float ndh = clamp(dot(n, h), 0.0, 1.0);
		float d = rough * rough;
		float denom = ndh * ndh * (d - 1.0) + 1.0;
		float ggx = d / max(PI * denom * denom, 0.0001);
		float lobe = mix(smoothstep(0.5, 0.51, ggx), ggx, clamp(specular_area, 0.0, 1.0));
		SPECULAR_LIGHT += LIGHT_COLOR * lobe * specular_intensity
		                * mix(vec3(1.0), ALBEDO, METALLIC) * ATTENUATION / PI;
	}
}
