// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The near-eye cut: fluid closer to the eye being drawn than near_eye_radius is not drawn, so nobody can blind
// anyone with goo. It is a discard, and a discard in an opaque draw costs early depth rejection on tile GPUs, so
// the plain shaders never carry it: the renderer draws its eyes-only twin with a Near variant only while the head
// is within reach of the fluid. VERTEX is in the view space of the camera, and under multiview each eye sits at
// EYE_OFFSET from it, so each eye cuts around itself. The last 5 cm fade out through an ordered dither, so the
// edge of the cut does not read as a hard line. -xlinka

// m; a shader that includes this without FLUID_NEAR_EYE starts with the cut off
#ifdef FLUID_NEAR_EYE
uniform float near_eye_radius = 0.15;
#else
uniform float near_eye_radius = 0.0;
#endif

float fluid_bayer4(vec2 frag)
{
	int i = (int(frag.x) & 3) + (int(frag.y) & 3) * 4;
	const float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
	return (m[i] + 0.5) / 16.0;
}

bool fluid_near_eye_cut(vec3 vertex, vec3 eye_offset, vec2 frag)
{
	float d = length(vertex - eye_offset);
	if (d >= near_eye_radius)
		return false;
	float inside = clamp((near_eye_radius - d) / 0.05, 0.0, 1.0);
	return inside > fluid_bayer4(frag);
}
