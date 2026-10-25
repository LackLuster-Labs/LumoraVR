shader_type spatial;
// Stencil-TESTED content variant of UI_Unlit. Identical to UI_Unlit except it only draws where the stencil
// buffer equals the mask reference value (written first by UI_StencilWrite). That's what clips content to a
// mask's exact SHAPE (circle/rounded/arbitrary geometry) instead of an axis-aligned rect. Drawn AFTER the
// mask writer (the chunk's render_priority ordering guarantees this). Godot 4.5+ stencil_mode. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_front;
stencil_mode read, compare_equal, 1;

uniform vec4 albedo_color : source_color = vec4(1.0);
uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform bool use_albedo_texture = false;

uniform vec2 uv_scale = vec2(1.0);
uniform vec2 uv_offset = vec2(0.0);
// A texture that is a sheet of frames, frame_columns to a row: the quad's UV covers the first frame and the
// clock picks which one shows, so an animated picture costs no mesh update. One frame means a still. -xlinka
uniform int frame_count = 1;
uniform int frame_columns = 1;
uniform float frame_rate = 10.0;
uniform bool use_vertex_color = true;

uniform bool alpha_clip = false;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.01;

// Rect clip stays available so a stencil-shaped mask can ALSO carry a rectangular bound (stencil = shape,
// rect = orthogonal AABB bound, applied together). -xlinka
uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);
// Scroll offset, same as UI_Unlit: a masked picture inside a scrolled list has to slide with the rest of
// its chunk. Without it the frame and the text moved and the art, chips and letter stayed behind. -xlinka
uniform vec2 clip_offset = vec2(0.0);

uniform int color_mask = 15;

varying vec2 local_pos;

float rect_alpha(vec2 pos, vec4 clip_rect) {
	vec2 edge = max(fwidth(pos), vec2(0.0001));
	float left = smoothstep(clip_rect.x - edge.x, clip_rect.x + edge.x, pos.x);
	float bottom = smoothstep(clip_rect.y - edge.y, clip_rect.y + edge.y, pos.y);
	float right = 1.0 - smoothstep(clip_rect.z - edge.x, clip_rect.z + edge.x, pos.x);
	float top = 1.0 - smoothstep(clip_rect.w - edge.y, clip_rect.w + edge.y, pos.y);
	return clamp(left * bottom * right * top, 0.0, 1.0);
}

void vertex() {
	VERTEX.xy += clip_offset;
	local_pos = VERTEX.xy;
	UV = UV * uv_scale + uv_offset;
}

void fragment() {
	float clip_alpha = rect_clip ? rect_alpha(local_pos, rect) : 1.0;

	vec2 uv = UV;
	if (frame_count > 1) {
		int columns = clamp(frame_columns, 1, frame_count);
		int rows = (frame_count + columns - 1) / columns;
		int frame = int(floor(mod(TIME * frame_rate, float(frame_count))));
		uv += vec2(float(frame % columns) / float(columns), float(frame / columns) / float(rows));
	}
	vec4 tex = use_albedo_texture ? texture(albedo_texture, uv) : vec4(1.0);
	vec4 out_color = albedo_color * tex;

	if (use_vertex_color) {
		out_color *= COLOR;
	}

	if (alpha_clip) {
		float alpha_width = max(fwidth(out_color.a), 0.001);
		out_color.a *= smoothstep(alpha_cutoff - alpha_width, alpha_cutoff + alpha_width, out_color.a);
	}

	out_color.a *= clip_alpha;

	if ((color_mask % 2) == 0) out_color.r = 0.0;
	if (((color_mask / 2) % 2) == 0) out_color.g = 0.0;
	if (((color_mask / 4) % 2) == 0) out_color.b = 0.0;
	if (((color_mask / 8) % 2) == 0) out_color.a = 0.0;

	ALBEDO = out_color.rgb;
	ALPHA = out_color.a;
}
