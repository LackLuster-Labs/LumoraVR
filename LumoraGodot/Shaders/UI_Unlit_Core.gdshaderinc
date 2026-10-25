// Shared body of the UI image material.
//
// UI_Unlit (read from its face only), UI_UnlitZWrite (a panel backing that writes depth) and UI_UnlitOverlay
// (drawn over everything) include this, and so does the source a UI material writes for a state those three
// do not cover: the other face, both faces, or a depth bias. One body keeps the uniforms of every variant in
// lockstep, so values set on the material survive a swap between them.
//
// Helio winds its quads so the side a canvas is read from is Godot's back face: cull_front shows the reading
// side only, cull_back the side behind it.
//
// Defines a variant can set before including:
//   DEPTH_OFFSET   writes a biased depth, see DepthOffset.gdshaderinc
//   BACKING_PUSH   moves the surface a few millimetres away from the camera so the panel's own coplanar UI
//                  still passes the depth test against the depth it lays down
//   HIDDEN_TINT    multiplies what lies behind the scene's opaque depth by hidden_tint
// -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif
#include "res://Shaders/TextureProfile.gdshaderinc"

#ifdef HIDDEN_TINT
uniform sampler2D scene_depth : hint_depth_texture, filter_nearest, repeat_disable;
uniform vec4 hidden_tint : source_color = vec4(1.0, 1.0, 1.0, 0.73);
#endif

// 0 shows the picture's colours, 1 shows a normal map as its directions. The sampler decodes colour, so a
// normal map is put back the way it was stored before its axes are read.
uniform int texture_mode = 0;

uniform vec4 albedo_color : source_color = vec4(1.0);
// Mipmapped AND anisotropic. Plain trilinear drops to a coarse mip at a glancing angle (the screen-space
// derivatives grow along the foreshortened axis), so rounded-rect sprites lost their shape and tinted
// grounds washed toward grey; aniso picks the level off the short axis and keeps them. Mips stay so a far
// icon does not shimmer. - xlinka
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

// Anything fainter than the cutoff is not drawn at all, depth included: a backing with see-through corners
// lays down depth only where it shows.
uniform bool alpha_clip = false;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.01;

// A second picture whose alpha cuts this one, on the quad's own UV: multiplied in (mask_mode 0) or as a hard
// edge at the cutoff (mask_mode 1).
uniform sampler2D mask_texture : source_color, filter_linear_mipmap_anisotropic, repeat_disable;
uniform bool use_mask_texture = false;
uniform vec2 mask_scale = vec2(1.0);
uniform vec2 mask_offset = vec2(0.0);
uniform int mask_mode = 0;

uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);
// Added to the vertex position before the rect test. For scrolled content whose chunk is moved by a
// transform (not re-tessellated), rect stays fixed in canvas space and this carries the scroll offset,
// so the clip window stays pinned to the viewport while content slides under it. Zero for static UI. -xlinka
uniform vec2 clip_offset = vec2(0.0);

uniform int color_mask = 15;
uniform int stencil_comparison = 8;
uniform int stencil_operation = 0;
uniform int stencil_id = 0;
uniform int stencil_write_mask = 255;
uniform int stencil_read_mask = 255;
uniform int z_write = 0;
uniform int z_test = 4;

#ifdef BACKING_PUSH
// View-space push away from the camera (meters) so the panel's own coplanar UI wins the depth test.
uniform float depth_bias = 0.004;
#endif

varying vec2 local_pos;
varying vec2 mask_uv;

float rect_alpha(vec2 pos, vec4 clip_rect) {
	vec2 edge = max(fwidth(pos), vec2(0.0001));
	float left = smoothstep(clip_rect.x - edge.x, clip_rect.x + edge.x, pos.x);
	float bottom = smoothstep(clip_rect.y - edge.y, clip_rect.y + edge.y, pos.y);
	float right = 1.0 - smoothstep(clip_rect.z - edge.x, clip_rect.z + edge.x, pos.x);
	float top = 1.0 - smoothstep(clip_rect.w - edge.y, clip_rect.w + edge.y, pos.y);
	return clamp(left * bottom * right * top, 0.0, 1.0);
}

void vertex() {
	// Scroll offset: move the vertex AND clip against it from the SAME uniform, so a scrolled chunk's
	// position and clip window can never desync (that caused content to leak past the viewport). -xlinka
	VERTEX.xy += clip_offset;
	local_pos = VERTEX.xy;
	mask_uv = UV * mask_scale + mask_offset;
	UV = UV * uv_scale + uv_offset;
#ifdef BACKING_PUSH
	// View space looks down -Z: subtracting pushes the vertex farther from the camera.
	VERTEX = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	VERTEX.z -= depth_bias;
	POSITION = PROJECTION_MATRIX * vec4(VERTEX, 1.0);
#endif
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
	if (texture_mode == 1) {
		vec2 axes = profile_encode(tex.rgb).rg * 2.0 - 1.0;
		tex = vec4(vec3(axes, sqrt(max(1.0 - dot(axes, axes), 0.0))) * 0.5 + 0.5, 1.0);
	}
	vec4 out_color = albedo_color * tex;

	if (use_vertex_color) {
		out_color *= COLOR;
	}

	if (use_mask_texture) {
		float mask = texture(mask_texture, mask_uv).a;
		if (mask_mode == 1) {
			if (mask < alpha_cutoff) {
				discard;
			}
		} else {
			out_color.a *= mask;
		}
	}

	if (alpha_clip && out_color.a < alpha_cutoff) {
		discard;
	}

	out_color.a *= clip_alpha;

#ifdef HIDDEN_TINT
	// Reversed depth: what is farther than the scene's opaque surface here stores less.
	if (FRAGCOORD.z < texture(scene_depth, SCREEN_UV).r) {
		out_color *= hidden_tint;
	}
#endif

	if ((color_mask % 2) == 0) out_color.r = 0.0;
	if (((color_mask / 2) % 2) == 0) out_color.g = 0.0;
	if (((color_mask / 4) % 2) == 0) out_color.b = 0.0;
	if (((color_mask / 8) % 2) == 0) out_color.a = 0.0;

	ALBEDO = out_color.rgb;
	ALPHA = out_color.a;
#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
