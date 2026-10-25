// A ring segment drawn on a quad that carries its shape per vertex, so one material serves every segment of a
// radial menu. The data, as a segment mesh lays it out:
//   UV          the point relative to the ring's centre, before the segment's turn
//   UV2         x the segment's start angle, y its sweep, radians
//   UV3 (CUSTOM1.xy)  x the inner radius, y the outer
//   UV4 (CUSTOM1.zw)  x the outline thickness, y the corner rounding
//   COLOR       the fill
// The outline is outline_tint alone: a vertex tangent cannot carry a colour here, its direction is packed on the
// way to the GPU. Edges are anti-aliased over one pixel of the distance to them.
//
// Mat_UICircleSegment is the alpha-blended file; every other state is source the material writes over this body
// (SpecialMaterialSource). Defines a variant sets before including:
//   TRANSPARENT_PASS  writes ALPHA
//   BLEND_ADDITIVE    adds the colour whatever its alpha
//   SEGMENT_OVERLAY   tints what is hidden behind the scene by overlay_tint, for a menu drawn through walls
//   ZTEST_NEVER       draws nothing
//   DEPTH_OFFSET      writes a biased depth, see DepthOffset.gdshaderinc
// -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform vec4 fill_tint : source_color = vec4(1.0);
uniform vec4 outline_tint : source_color = vec4(1.0);
uniform vec4 overlay_tint : source_color = vec4(1.0, 1.0, 1.0, 0.73);

uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);
uniform int color_mask = 15;

#ifdef SEGMENT_OVERLAY
uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
#endif

const float SEGMENT_PI = 3.14159265;
const float SEGMENT_TAU = 6.28318531;

varying vec2 segment_uv;
varying vec2 segment_angles;
varying vec4 segment_extent;
varying vec2 segment_local;

void vertex() {
	segment_angles = UV2;
	segment_extent = CUSTOM1;
	segment_local = VERTEX.xy;
	// Turned so the middle of the sweep sits opposite the angle's zero, where atan() is continuous.
	float turn = UV2.x - (SEGMENT_PI - UV2.y * 0.5);
	float c = cos(turn);
	float s = sin(turn);
	segment_uv = vec2(UV.x * c - UV.y * s, UV.x * s + UV.y * c);
}

// How far inside an edge this pixel is, in pixels, clamped to 0..1. Within corner of two edges at once the
// distance is to a rounded corner instead.
float segment_coverage(float along_angle, float along_radius, float corner) {
	float dist;
	if (along_angle < corner && along_radius < corner) {
		dist = corner - length(vec2(corner - along_radius, corner - along_angle));
	} else {
		dist = min(along_angle, along_radius);
	}
	return clamp(dist / max(fwidth(dist), 1e-6), 0.0, 1.0);
}

void fragment() {
#ifdef ZTEST_NEVER
	discard;
#endif
	if (rect_clip && (segment_local.x < rect.x || segment_local.y < rect.y || segment_local.x > rect.z || segment_local.y > rect.w)) {
		discard;
	}

	float sweep = segment_angles.y;
	float inner = segment_extent.x;
	float outer = segment_extent.y;
	float thickness = segment_extent.z;

	float radius = length(segment_uv);
	// 0 at the start of the sweep, the sweep at its end, once the vertex turn is taken back off.
	float angle = atan(-segment_uv.y, segment_uv.x) + sweep * 0.5;
	// Distances to the two straight edges, as arc length at this radius, and to the two curved ones.
	float along_angle = min(angle, sweep - angle) * radius;
	float along_radius = min(radius - inner, outer - radius);

	// What is left of the circle past the sweep caps how wide the outline and the rounding can grow, so a near
	// full ring does not eat its own gap.
	float gap = (SEGMENT_TAU - sweep) * inner;
	float corner = min(segment_extent.w, gap);
	float outline = min(thickness, gap);
	along_angle += max(0.0, thickness - outline);

	float edge = segment_coverage(along_angle, along_radius, corner);
	if (edge <= 0.0) {
		discard;
	}
	float fill = segment_coverage(along_angle - outline, along_radius - thickness, corner);

	vec4 rim = outline_tint;
	rim.a *= edge;
	vec4 c = mix(rim, COLOR * fill_tint, fill);

#ifdef SEGMENT_OVERLAY
	float raw = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	float scene_z = -(unprojected.z / unprojected.w);
	if (-VERTEX.z > scene_z) {
		c *= overlay_tint;
	}
#endif

	if ((color_mask % 2) == 0) c.r = 0.0;
	if (((color_mask / 2) % 2) == 0) c.g = 0.0;
	if (((color_mask / 4) % 2) == 0) c.b = 0.0;
	if (((color_mask / 8) % 2) == 0) c.a = 0.0;

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
