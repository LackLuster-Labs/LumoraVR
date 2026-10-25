shader_type spatial;
// Atmosphere sky material. Each fragment shows the sky in the world direction from the eye through it, worked
// exactly as Sky_Atmosphere works it, so a mesh wearing this is a window onto that same sky. Unshaded and
// opaque: writes no ALPHA, so it stays in the OPAQUE pass. -xlinka
render_mode unshaded, fog_disabled, blend_mix, cull_back, depth_draw_opaque;

#include "res://Shaders/AtmosphereSky_Core.gdshaderinc"

void fragment() {
	// VERTEX sits in the shared view space and EYE_OFFSET is the eye drawing it, so each eye looks through the
	// surface along its own line and the sky stays at infinity in stereo.
	vec3 view_dir = normalize(mat3(INV_VIEW_MATRIX) * (VERTEX - EYE_OFFSET));
	ALBEDO = atmosphere_sky(view_dir, true);
}
