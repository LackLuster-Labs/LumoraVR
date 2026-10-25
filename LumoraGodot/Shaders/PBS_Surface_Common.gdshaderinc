// Shared pieces of the generated PBS surfaces: two-sided, rim lit, intersect glow, two-layer, multi-UV and
// splat map. Each family's body declares its own inputs, gathers the surface into the pbs_* locals and ends
// fragment() with PBS_Surface_Write.gdshaderinc. The variant source (PBSSurfaceSource) names the render
// modes and which of these it wants:
//
//   PBS_SPECULAR      reflectance comes as a colour with smoothness in alpha, lit by a light() of its own
//   TRANSPARENT_PASS  writes ALPHA, which on its own is what files a Godot material in the blended pass
//   PREMULTIPLIED     with blend_premul_alpha: only the diffuse fades with alpha, reflections and emission
//                     stay at full strength, which is how glass reads. Without it alpha fades everything.
//   ALPHA_CLIP        discards under alpha_clip. Kept out of every other variant: a discard anywhere in a
//                     shader costs it Godot's shared depth and shadow material.
//   DEPTH_OFFSET      a biased depth, DepthOffset.gdshaderinc
//
// Occlusion only darkens the light that arrives from the surroundings (ambient and reflections); direct
// lights stay as they are. That is the difference between a baked cavity map and a shadow. -xlinka

#ifdef DEPTH_OFFSET
#include "res://Shaders/DepthOffset.gdshaderinc"
#endif

#ifdef PBS_SPECULAR
#include "res://Shaders/PBS_Specular_Light.gdshaderinc"
#endif
#include "res://Shaders/TextureProfile.gdshaderinc"

uniform float alpha_clip : hint_range(0.0, 1.0) = 0.5;

// Tangent-space normal from a map: the scale leans only the slope, never the height.
vec3 pbs_unpack_normal(vec4 packed_normal, float scale) {
	vec3 n = packed_normal.xyz * 2.0 - 1.0;
	n.xy *= scale;
	return n;
}

// Two tangent-space normals layered: slopes add, heights multiply.
vec3 pbs_blend_normals(vec3 n1, vec3 n2) {
	return normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
}

// A ratio along a transition that may have zero width. A zero-width band is a hard step at its start, which
// is what a division by zero saturating to 0 or 1 would have given; written out so it never makes a NaN.
float pbs_ramp(float value, float from, float to) {
	float span = to - from;
	if (abs(span) < 1e-7) {
		return value - from > 0.0 ? 1.0 : 0.0;
	}
	return clamp((value - from) / span, 0.0, 1.0);
}
