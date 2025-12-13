// Lumora Unlit Transparent Shader
// Simple unlit/emissive material with transparency support
// Godot Implementation

shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_alpha_prepass, cull_disabled;

// Color Properties
uniform vec4 albedo_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_albedo_texture = false;

// Texture Coordinate Properties
uniform vec2 uv_scale = vec2(1.0, 1.0);
uniform vec2 uv_offset = vec2(0.0, 0.0);

// Vertex Color Support
uniform bool use_vertex_color = false;

void vertex() {
	// Apply UV transformations
	UV = UV * uv_scale + uv_offset;
}

void fragment() {
	// Calculate final UVs
	vec2 base_uv = UV;

	// --- ALBEDO ---
	vec4 albedo_tex = use_albedo_texture ? texture(albedo_texture, base_uv) : vec4(1.0);
	vec4 final_color = albedo_color * albedo_tex;

	// Apply vertex color if enabled
	if (use_vertex_color) {
		final_color *= COLOR;
	}

	ALBEDO = final_color.rgb;
	ALPHA = final_color.a;
}
