// Shared body of world-space text (nameplates, labels, signs). Same glyph coverage path as
// UI_Text.gdshader but WITH depth testing so text occludes correctly behind world geometry.
//
// Text_Unlit.gdshader draws both faces with no stencil, which is what nearly all text wants. A text material
// with culling, a stencil state or a colour mask that hides its colour writes its own few lines of render
// state over this body (TextMaterial), compiled once per distinct state.
//
// Defines a variant can set before including:
//   TEXT_STAMP    only what lies inside the glyphs is drawn, so a stencil write marks the letters and not
//                 the quads they sit on
//   TEXT_HIDDEN   the colour mask drops red, green and blue: nothing shows, the stencil and depth still go
//   DEPTH_OFFSET  writes a biased depth, see DepthOffset.gdshaderinc
// -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform vec4 albedo_color : source_color = vec4(1.0);
// Plain bilinear, no mipmaps, no aniso - see UI_Text.gdshader for why mip
// selection at glancing angles destroys glyph alpha. - xlinka
uniform sampler2D albedo_texture : filter_linear, repeat_disable;
uniform bool use_albedo_texture = true;
uniform bool use_vertex_color = true;

uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);

// MSDF distance-field span in atlas texels (the font's msdf_pixel_range); drives the screen-space edge width
// in the MSDF branch below. Set by the material from the atlas. - xlinka
uniform float pixel_range = 8.0;
uniform float coverage_gamma : hint_range(0.5, 2.0) = 0.9;
// The shared font atlas can be MSDF (distance field in RGB, alpha left opaque) OR plain coverage (shape in
// alpha). Nameplates/labels render off the SAME atlas as Helio UI text, so branch per material: MSDF rebuilds
// the glyph from median(RGB), coverage reads alpha. Miss this on an MSDF atlas and alpha is 1 everywhere, so
// the whole quad fills solid - that is the "nametag is just a white block" regression. Set from atlas.IsMSDF. - xlinka
uniform bool use_msdf = false;

// Glyph outline for floating text, a ring of outline_color outside the face, thickness in atlas texels;
// 0 disables it. The face lies over the ring, so a face with almost no alpha leaves the glyph hollow.
uniform vec4 outline_color : source_color = vec4(0.0);
uniform float outline_thickness = 0.0;
// Atlas texels the face edge moves out (bolder) or in, and texels of extra edge softening. Distance-field
// atlases only; a coverage atlas has no distance to move.
uniform float face_dilate = 0.0;
uniform float face_softness = 0.0;
// What fills the glyph quad outside the letter, tinted by the vertex colour like the face. Off, the edge
// fades out in the face's own colour. Distance-field atlases only.
uniform bool use_background_color = false;
uniform vec4 background_color : source_color = vec4(0.0);

// A band of light that sweeps across the letters every few seconds, for marks that are given rather than
// chosen (staff names). Strength 0 leaves the text exactly as it was. It runs in the text's own space off the
// clock, so it moves the same in a mirror or a camera and costs nothing on the CPU. -xlinka
uniform float shimmer = 0.0;
uniform vec4 shimmer_color : source_color = vec4(1.0);

// Crisp text at any distance/angle: smoothstep across the half-coverage
// threshold using fwidth so the edge stays ~1px wide in screen space. - xlinka
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
uniform bool alpha_clip = true;

uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);

uniform int color_mask = 15;
uniform int stencil_comparison = 8;
uniform int stencil_operation = 0;
uniform int stencil_id = 0;
uniform int stencil_write_mask = 255;
uniform int stencil_read_mask = 255;
uniform int z_write = 0;
uniform int z_test = 4;

varying vec2 local_pos;
// Per-glyph edge push for bold letters, from UV2.x, in shares of the distance field; 0 for regular text.
varying float glyph_dilate;

float rect_alpha(vec2 pos, vec4 clip_rect) {
	vec2 edge = max(fwidth(pos), vec2(0.0001));
	float left = smoothstep(clip_rect.x - edge.x, clip_rect.x + edge.x, pos.x);
	float bottom = smoothstep(clip_rect.y - edge.y, clip_rect.y + edge.y, pos.y);
	float right = 1.0 - smoothstep(clip_rect.z - edge.x, clip_rect.z + edge.x, pos.x);
	float top = 1.0 - smoothstep(clip_rect.w - edge.y, clip_rect.w + edge.y, pos.y);
	return clamp(left * bottom * right * top, 0.0, 1.0);
}

float median3(float a, float b, float c) {
	return max(min(a, b), min(max(a, b), c));
}

// Screen pixels the atlas's pixel_range spans at this fragment. Converts the distance field's texel range into
// screen space so the smoothstep edge stays ~1px wide however big/small/angled the glyph is drawn - keeps
// world-space MSDF text crisp without mipmaps, same as UI_Text. - xlinka
float msdf_screen_range(vec2 uv) {
	vec2 unit_range = vec2(pixel_range) / vec2(textureSize(albedo_texture, 0));
	vec2 screen_tex_size = vec2(1.0) / max(fwidth(uv), vec2(1e-6));
	return max(0.5 * dot(unit_range, screen_tex_size), 1.0);
}

// Four samples once a pixel spans more than two atlas texels, same as UI_Text.gdshader. -xlinka
float msdf_face(vec2 uv, float edge, float aa, float softness) {
	vec2 dx = dFdx(uv);
	vec2 dy = dFdy(uv);
	vec2 atlas = vec2(textureSize(albedo_texture, 0));
	if (max(length(dx * atlas), length(dy * atlas)) < 2.0) {
		vec3 m = textureLod(albedo_texture, uv, 0.0).rgb;
		return clamp((median3(m.r, m.g, m.b) - edge) / (aa + softness) + 0.5, 0.0, 1.0);
	}
	float spread = aa * 0.5 + softness;
	vec3 a = textureLod(albedo_texture, uv + dx * 0.125 + dy * 0.375, 0.0).rgb;
	vec3 b = textureLod(albedo_texture, uv - dx * 0.125 - dy * 0.375, 0.0).rgb;
	vec3 c = textureLod(albedo_texture, uv + dx * 0.375 - dy * 0.125, 0.0).rgb;
	vec3 d = textureLod(albedo_texture, uv - dx * 0.375 + dy * 0.125, 0.0).rgb;
	return 0.25 * (clamp((median3(a.r, a.g, a.b) - edge) / spread + 0.5, 0.0, 1.0)
		+ clamp((median3(b.r, b.g, b.b) - edge) / spread + 0.5, 0.0, 1.0)
		+ clamp((median3(c.r, c.g, c.b) - edge) / spread + 0.5, 0.0, 1.0)
		+ clamp((median3(d.r, d.g, d.b) - edge) / spread + 0.5, 0.0, 1.0));
}

void vertex() {
	local_pos = VERTEX.xy;
	glyph_dilate = UV2.x;
	UV = UV * uv_scale + uv_offset;
}

void fragment() {
	float clip_alpha = rect_clip ? rect_alpha(local_pos, rect) : 1.0;

	vec4 out_color = albedo_color;
	if (use_vertex_color) {
		out_color *= COLOR;
	}

	// How much of this fragment is glyph, for the stamp below.
	float shape = 1.0;
	if (use_msdf) {
		// Distance-field glyph: rebuild coverage from the median of the 3 MSDF channels, anti-aliased in
		// SCREEN space so the edge stays ~1px at any size/distance. Alpha is opaque on an MSDF atlas, so the
		// coverage=texture().a path below would fill the whole quad solid (that was the white-block bug). - xlinka
		float range_px = max(pixel_range, 0.0001);
		// Shifts are in atlas texels; one texel is 1 / pixel_range of the field. The face and its ring stay
		// within 0.35 of the edge: past that the threshold drops under the field's floor at the glyph quad's
		// padding and the "ring" becomes the entire quad - a nameplate row of solid boxes. -xlinka
		float dilate = clamp(face_dilate / range_px + glyph_dilate, -0.45, 0.3);
		float ring = clamp(outline_thickness / range_px, 0.0, 0.35 - max(dilate, 0.0));
		float aa = 1.0 / msdf_screen_range(UV);
		float softness = max(face_softness, 0.0) / range_px;
		float face_edge = 0.5 - dilate;
		float face = msdf_face(UV, face_edge, aa, softness);
		shape = face;

		if (outline_thickness > 0.0) {
			float outer = msdf_face(UV, face_edge - ring, aa, softness);
			shape = outer;
			// Inside the face the face colour shows, in the ring the outline's, and the alpha follows the
			// colour that shows: a face with almost no alpha leaves an outlined glyph hollow. -xlinka
			vec4 ringed = mix(out_color, outline_color, 1.0 - face);
			out_color = vec4(ringed.rgb, ringed.a * outer);
		} else {
			out_color.a *= face;
		}

		if (use_background_color) {
			vec4 bg = use_vertex_color ? background_color * COLOR : background_color;
			out_color = vec4(mix(bg.rgb, out_color.rgb, shape), out_color.a + bg.a * (1.0 - shape));
		}
	} else {
		float coverage = 1.0;
		if (use_albedo_texture) {
			coverage = texture(albedo_texture, UV).a;
			coverage = pow(clamp(coverage, 0.0, 1.0), coverage_gamma);
			// Crisped on the glyph's own coverage, before the colour, same as UI_Text.gdshader. -xlinka
			if (alpha_clip) {
				float width = max(fwidth(coverage), 0.001);
				coverage *= smoothstep(alpha_cutoff - width, alpha_cutoff + width, coverage);
			}
		}
		out_color.a *= coverage;

		// Outline post-step: leaves the glyph face untouched (face_a dominates where the glyph is
		// solid) and fills the dilated ring around it with outline_color. Gated on thickness.
		if (outline_thickness > 0.0 && use_albedo_texture) {
			vec2 ts = outline_thickness / vec2(textureSize(albedo_texture, 0));
			float dil = 0.0;
			dil = max(dil, texture(albedo_texture, UV + vec2(ts.x, 0.0)).a);
			dil = max(dil, texture(albedo_texture, UV + vec2(-ts.x, 0.0)).a);
			dil = max(dil, texture(albedo_texture, UV + vec2(0.0, ts.y)).a);
			dil = max(dil, texture(albedo_texture, UV + vec2(0.0, -ts.y)).a);
			dil = max(dil, texture(albedo_texture, UV + ts).a);
			dil = max(dil, texture(albedo_texture, UV - ts).a);
			dil = max(dil, texture(albedo_texture, UV + vec2(ts.x, -ts.y)).a);
			dil = max(dil, texture(albedo_texture, UV + vec2(-ts.x, ts.y)).a);
			float out_a = pow(clamp(dil, 0.0, 1.0), coverage_gamma) * outline_color.a;
			float face_a = out_color.a;
			float comb_a = max(face_a, out_a);
			vec3 comb_rgb = mix(outline_color.rgb, out_color.rgb, face_a / max(comb_a, 0.0001));
			out_color = vec4(comb_rgb, comb_a);
		}
		shape = out_color.a;
	}

	out_color.a *= clip_alpha;

	if (shimmer > 0.0) {
		// Metres along the line, leaning a little so the band reads as light rather than a wipe. The sweep
		// overshoots both ends so the name rests unlit between passes.
		float sweep = fract(TIME * 0.4) * 1.6 - 0.8;
		float d = (local_pos.x + local_pos.y * 0.5 - sweep) / 0.06;
		float band = exp(-d * d);
		out_color.rgb = mix(out_color.rgb, shimmer_color.rgb, clamp(band * shimmer, 0.0, 1.0) * 0.75);
	}

#ifdef TEXT_STAMP
	if (shape * clip_alpha < 0.5) {
		discard;
	}
#endif

	if ((color_mask % 2) == 0) out_color.r = 0.0;
	if (((color_mask / 2) % 2) == 0) out_color.g = 0.0;
	if (((color_mask / 4) % 2) == 0) out_color.b = 0.0;
	if (((color_mask / 8) % 2) == 0) out_color.a = 0.0;

#if !defined(TEXT_STAMP) && !defined(TEXT_HIDDEN)
	// Empty atlas padding and glyph holes must not write depth. Keep partially covered edge pixels
	// blended; stencil-only text uses the shape test above even when its colour is invisible.
	if (out_color.a <= 0.001) {
		discard;
	}
#endif

	ALBEDO = out_color.rgb;
#ifdef TEXT_HIDDEN
	ALPHA = 0.0;
#else
	ALPHA = out_color.a;
#endif
#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
