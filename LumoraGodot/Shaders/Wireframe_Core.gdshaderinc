// Shared body of the wireframe: edge lines over an optional filled face.
//
// Barycentric coordinates arrive in the vertex COLOR channel. Godot 4 has no geometry shader and no
// barycentric builtin, and the mesh upload path carries only position/normal/tangent/color/uv0/index,
// so COLOR is the only per-vertex slot a triangle-local coordinate can ride in. Bake it with the mesh's
// "Bake Wireframe Barycentrics" action, which unwelds the triangles first (shared vertices cannot carry
// three different corner values). Without a bake there are no wires, only the fill. -xlinka
//
// Mat_Wireframe and Mat_WireframeOverlay are the two-sided, never depth-written states, with and without the
// depth test. Culling, a depth write or a depth bias come as generated source over this body
// (WireframeMaterial), DEPTH_OFFSET defined for the bias.

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

uniform bool use_vertex_barycentric = true;

uniform vec4 line_color : source_color = vec4(1.0);
uniform vec4 fill_color : source_color = vec4(0.0, 0.0, 0.0, 0.0);

// Screen space: line_width pixels, the line fading out across its width. Otherwise a crisp line thickness
// metres in from each edge.
uniform bool screen_space = true;
uniform float line_width : hint_range(0.1, 20.0) = 1.5;
uniform float thickness = 0.001;

// Toward the silhouette the colours swing to the far ones; the exponent pins the swing to the edge.
uniform bool use_fresnel = false;
uniform float fresnel_exponent = 1.0;
uniform vec4 line_far_color : source_color = vec4(1.0);
uniform vec4 fill_far_color : source_color = vec4(1.0, 1.0, 1.0, 0.0);

// Back faces in the inner colours.
uniform bool use_inner_colors = false;
uniform vec4 inner_line_color : source_color = vec4(0.0, 0.0, 0.0, 0.73);
uniform vec4 inner_fill_color : source_color = vec4(0.0, 0.0, 0.0, 0.0);
uniform vec4 inner_line_far_color : source_color = vec4(1.0, 1.0, 1.0, 0.73);
uniform vec4 inner_fill_far_color : source_color = vec4(1.0, 1.0, 1.0, 0.0);

uniform sampler2D fill_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_fill_texture = false;
// The texture multiplies the lines as well as the fill.
uniform bool texture_tints_lines = false;
uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);

void vertex() {
	UV = UV * texture_scale + texture_offset;
}

void fragment() {
	bool inner = use_inner_colors && !FRONT_FACING;
	vec4 line = inner ? inner_line_color : line_color;
	vec4 fill = inner ? inner_fill_color : fill_color;
	if (use_fresnel) {
		float f = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), fresnel_exponent);
		line = mix(line, inner ? inner_line_far_color : line_far_color, f);
		fill = mix(fill, inner ? inner_fill_far_color : fill_far_color, f);
	}

	vec4 tex = use_fill_texture ? texture(fill_texture, UV) : vec4(1.0);
	fill *= tex;
	if (texture_tints_lines) {
		line *= tex;
	}

	float wire = 0.0;
	if (use_vertex_barycentric) {
		vec3 bary = COLOR.rgb;
		if (screen_space) {
			// Screen-space width: fwidth of the barycentric gives how much it changes per pixel, so
			// dividing by it turns the coordinate into a pixel distance to the nearest edge. Constant
			// line thickness at any distance or angle without a thickness uniform in world units.
			vec3 d = fwidth(bary);
			vec3 edge = smoothstep(vec3(0.0), d * line_width, bary);
			wire = 1.0 - min(min(edge.x, edge.y), edge.z);
		} else {
			// Metres to each edge: a barycentric over its gradient along the surface. The gradient comes from
			// the screen derivatives of the coordinate and of the position, through the inverse of the
			// position derivatives' Gram matrix, so it holds at any angle. -xlinka
			vec3 dpx = dFdx(VERTEX);
			vec3 dpy = dFdy(VERTEX);
			float xx = dot(dpx, dpx);
			float xy = dot(dpx, dpy);
			float yy = dot(dpy, dpy);
			float det = max(xx * yy - xy * xy, 1e-24);
			vec3 bx = dFdx(bary);
			vec3 by = dFdy(bary);
			vec3 gradient = sqrt(max((yy * bx * bx - 2.0 * xy * bx * by + xx * by * by) / det, vec3(1e-24)));
			vec3 dist = bary / gradient;
			float nearest = min(min(dist.x, dist.y), dist.z);
			float aa = fwidth(nearest);
			wire = 1.0 - smoothstep(thickness - aa, thickness, nearest);
		}
	}

	vec4 out_color = mix(fill, line, wire);
	ALBEDO = out_color.rgb;
	ALPHA = out_color.a;

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
}
