// Shared body of the matcap material. Mat_Matcap is the blended, depth pre-passed state; an opaque, additive,
// multiplied or premultiplied one comes as generated source over this body (MatcapMaterial), and only a
// blended one defines TRANSPARENT_PASS and carries its alpha. -xlinka

// Sampled by the view-space normal, so the lit side follows the camera and never the world.
uniform sampler2D matcap_texture : source_color, filter_linear_mipmap, repeat_disable;
uniform bool use_matcap_texture = false;
uniform vec2 matcap_scale = vec2(1.0);
uniform vec2 matcap_offset = vec2(0.0);

uniform vec4 tint_color : source_color = vec4(1.0);

uniform sampler2D base_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_base_texture = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

uniform bool alpha_clip = false;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

void vertex() {
	UV = UV * texture_scale + texture_offset;
}

void fragment() {
	// NORMAL, TANGENT and BINORMAL are all view-space here, so the perturbed normal comes out in view
	// space too and can index the matcap directly.
	vec3 n = NORMAL;
	if (use_normal_map) {
		vec3 tn = texture(normal_map, UV).xyz * 2.0 - 1.0;
		tn.xy *= normal_scale;
		n = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}

	vec4 out_color = tint_color;
	if (use_matcap_texture) {
		out_color *= texture(matcap_texture, (n.xy * 0.5 + 0.5) * matcap_scale + matcap_offset);
	}
	if (use_base_texture) {
		out_color *= texture(base_texture, UV);
	}

	if (alpha_clip && out_color.a < alpha_cutoff) {
		discard;
	}

	ALBEDO = out_color.rgb;
#ifdef TRANSPARENT_PASS
	ALPHA = out_color.a;
#endif
}
