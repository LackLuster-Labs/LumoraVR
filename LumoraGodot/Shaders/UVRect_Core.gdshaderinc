// A rectangle in the surface's UV square: inner_color inside rect, outer_color outside, with a hard edge between
// them. rect and clip_rect are (min u, min v, max u, max v) on this side's UVs, V down from the top. With
// RECT_CLIP everything outside clip_rect is cut away.
//
// Mat_UVRect is the opaque file; every other state is source the material writes over this body
// (SpecialMaterialSource). Defines a variant sets before including:
//   TRANSPARENT_PASS  writes ALPHA
//   BLEND_ADDITIVE    adds the colour whatever its alpha
//   RECT_CLIP         discards outside clip_rect
//   ZTEST_NEVER       draws nothing
//   DEPTH_OFFSET      writes a biased depth, see DepthOffset.gdshaderinc
// A cutout blend draws as opaque: there is no alpha test in this one. -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform vec4 rect = vec4(0.25, 0.25, 0.75, 0.75);
uniform vec4 outer_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 inner_color : source_color = vec4(1.0);
uniform vec4 clip_rect = vec4(0.0, 0.0, 1.0, 1.0);

// 1 inside r, edges included, 0 outside.
float uv_rect_inside(vec2 uv, vec4 r) {
	vec2 inside = step(r.xy, uv) * step(uv, r.zw);
	return inside.x * inside.y;
}

void fragment() {
#ifdef ZTEST_NEVER
	discard;
#endif
#ifdef RECT_CLIP
	if (uv_rect_inside(UV, clip_rect) < 0.5) {
		discard;
	}
#endif

	vec4 c = mix(outer_color, inner_color, uv_rect_inside(UV, rect));

	ALBEDO = c.rgb;
#ifdef TRANSPARENT_PASS
#ifdef BLEND_ADDITIVE
	ALPHA = 1.0;
#else
	ALPHA = c.a;
#endif
#endif

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
