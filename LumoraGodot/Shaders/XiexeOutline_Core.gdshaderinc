// Shared body of the Xiexe toon outline: an inverted hull. The shell is pushed out along the normal and only
// its BACK faces survive, so it shows as a border wherever the real surface does not cover it. Godot 4 has no
// multi-pass shaders, so the material hook chains this on as the material's next pass.
//
// Emissive is Mat_XiexeOutline: unshaded, the flat outline colour. Lit comes as generated source with
// OUTLINE_LIT defined (XiexeToonMaterial): the outline colour shaded by each light over the shell's own
// normal, ambient included, the way a surface of that colour would be. -xlinka

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Metres of extrusion in MODEL space, so the border keeps a fixed thickness on the object and thins
// with distance the way a drawn line does. On a scaled slot it scales with the slot.
// Metres, so the useful range is millimetres. The flat toon outline's 0.1 ceiling belongs to a
// depth-scaled factor and means nothing here: at 0.1 this extrudes a ten centimetre shell.
uniform float outline_width : hint_range(0.0, 0.005) = 0.0;
// Scales the width per vertex and gates where the outline shows at all.
uniform sampler2D outline_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_outline_mask = false;
// The surface's albedo texture, on its own tiling, when the outline takes the albedo's tint.
uniform sampler2D outline_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_outline_texture = false;
uniform vec2 outline_texture_scale = vec2(1.0);
uniform vec2 outline_texture_offset = vec2(0.0);

#ifdef OUTLINE_LIT
varying vec3 shell_normal;
#endif

void vertex() {
	vec3 view_pos = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 view_normal = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
	float authored_width = outline_width * length((MODELVIEW_MATRIX * vec4(normalize(NORMAL), 0.0)).xyz);
	float pixel_size = 2.0 * max(-view_pos.z, 0.01) / (abs(PROJECTION_MATRIX[1][1]) * VIEWPORT_SIZE.y);
	float width = min(authored_width, min(0.002, 1.5 * pixel_size));
	if (use_outline_mask) {
		width *= textureLod(outline_mask, UV, 0.0).r;
	}
	view_pos += view_normal * width;
	POSITION = PROJECTION_MATRIX * vec4(view_pos, 1.0);
	// No width, no hull: every corner on one point outside the view rasterises nothing, where a discard
	// still shaded each back-face pixel in every pass. -xlinka
	if (outline_width <= 0.0) {
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	}
#ifdef OUTLINE_LIT
	shell_normal = view_normal;
#endif
}

void fragment() {
	// The mask gates WHERE the outline appears, so an author can keep it off the face and on the body.
	if (use_outline_mask && texture(outline_mask, UV).r < 0.5) {
		discard;
	}
	ALBEDO = outline_color.rgb;
	if (use_outline_texture) {
		ALBEDO *= texture(outline_texture, UV * outline_texture_scale + outline_texture_offset).rgb;
	}
#ifdef OUTLINE_LIT
	// The shell faces away from the viewer where it shows; it is lit by the way it faces, not by the normal
	// a back face gets turned to.
	NORMAL = normalize(shell_normal);
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
#endif
}

#ifdef OUTLINE_LIT
// Light reaching the shell, cut by shadow, and nothing else: no highlight. LIGHT_COLOR comes in scaled by PI.
void light() {
	DIFFUSE_LIGHT += LIGHT_COLOR * clamp(ATTENUATION * dot(NORMAL, LIGHT), 0.0, 1.0) / PI;
}
#endif
