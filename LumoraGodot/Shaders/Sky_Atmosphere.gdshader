shader_type sky;
// The atmosphere sky as the world's background (AtmosphereSkyMaterial on a Skybox). The sun disc is for the eye
// only: the radiance cubemap feeds ambient and reflections, and a disc in it shows up on every glossy floor as
// a hard hotspot. The air's own brightening toward the sun stays in. -xlinka

#include "res://Shaders/AtmosphereSky_Core.gdshaderinc"

void sky() {
	COLOR = atmosphere_sky(EYEDIR, !AT_CUBEMAP_PASS);
}
