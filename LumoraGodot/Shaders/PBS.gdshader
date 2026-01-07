// Lumora PBS (Physically Based Shading) Shader
// Lumora Standard Physically-Based Shading
// Godot Implementation

shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

// Albedo/Base Color Properties
uniform vec4 albedo_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_albedo_texture = false;

// Metallic Properties
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform sampler2D metallic_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_metallic_texture = false;

// Roughness/Smoothness Properties
// Note: Smoothness is inverted to roughness (roughness = 1.0 - smoothness)
uniform float smoothness : hint_range(0.0, 1.0) = 0.5;
uniform bool use_metallic_texture_smoothness = false;

// Normal Map Properties
uniform sampler2D normal_texture : hint_normal, filter_linear_mipmap, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(-16.0, 16.0) = 1.0;

// Emission Properties
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform sampler2D emission_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_emission_texture = false;
uniform float emission_energy : hint_range(0.0, 16.0) = 1.0;

// Ambient Occlusion Properties
uniform sampler2D ao_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_ao_texture = false;
uniform float ao_light_affect : hint_range(0.0, 1.0) = 0.0;

// Alpha/Transparency Properties
uniform float alpha_scissor_threshold : hint_range(0.0, 1.0) = 0.5;
uniform bool use_alpha_scissor = false;

// Texture Coordinate Properties
uniform vec2 uv_scale = vec2(1.0, 1.0);
uniform vec2 uv_offset = vec2(0.0, 0.0);

// Offset/Depth Properties (for render offset support)
uniform float offset_factor = 0.0;
uniform float offset_units = 0.0;

// Vertex Color Support
uniform bool use_vertex_color_albedo = false;
uniform bool use_vertex_color_emission = false;

void vertex() {
	// Apply UV transformations
	UV = UV * uv_scale + uv_offset;
}

void fragment() {
	// Calculate final UVs
	vec2 base_uv = UV;

	// --- ALBEDO ---
	vec4 albedo_tex = use_albedo_texture ? texture(albedo_texture, base_uv) : vec4(1.0);
	vec4 final_albedo = albedo_color * albedo_tex;

	// Apply vertex color to albedo if enabled
	if (use_vertex_color_albedo) {
		final_albedo *= COLOR;
	}

	// Alpha handling
	if (use_alpha_scissor) {
		if (final_albedo.a < alpha_scissor_threshold) {
			discard;
		}
	}

	ALBEDO = final_albedo.rgb;
	ALPHA = final_albedo.a;

	// --- METALLIC ---
	float final_metallic = metallic;
	float final_roughness = 1.0 - smoothness; // Convert smoothness to roughness

	if (use_metallic_texture) {
		vec4 metallic_tex = texture(metallic_texture, base_uv);
		final_metallic = metallic_tex.r;

		// If using metallic texture alpha channel for smoothness
		if (use_metallic_texture_smoothness) {
			final_roughness = 1.0 - metallic_tex.a;
		}
	}

	METALLIC = final_metallic;
	ROUGHNESS = final_roughness;

	// --- NORMAL MAPPING ---
	if (use_normal_map) {
		vec3 normal_tex = texture(normal_texture, base_uv).rgb;
		// Convert from texture space [0,1] to normal space [-1,1]
		normal_tex = normal_tex * 2.0 - 1.0;
		// Apply normal scale
		normal_tex.xy *= normal_scale;
		NORMAL_MAP = normal_tex;
		NORMAL_MAP_DEPTH = 1.0;
	}

	// --- EMISSION ---
	vec3 final_emission = emission_color.rgb;

	if (use_emission_texture) {
		vec3 emission_tex = texture(emission_texture, base_uv).rgb;
		final_emission *= emission_tex;
	}

	// Apply vertex color to emission if enabled
	if (use_vertex_color_emission) {
		final_emission *= COLOR.rgb;
	}

	EMISSION = final_emission * emission_energy;

	// --- AMBIENT OCCLUSION ---
	if (use_ao_texture) {
		float ao = texture(ao_texture, base_uv).r;
		AO = ao;
		AO_LIGHT_AFFECT = ao_light_affect;
	}

	// Apply specular mode (Godot's default is Schlick-GGX which matches Schlick-GGX specular model)
	SPECULAR = 0.5; // Default specular value for PBR
}
