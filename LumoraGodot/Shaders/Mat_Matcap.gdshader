shader_type spatial;
// Matcap lighting is baked into the texture, so the surface must not take scene light on top of it.
render_mode unshaded, blend_mix, depth_prepass_alpha, cull_back;

#define TRANSPARENT_PASS
#include "res://Shaders/Matcap_Core.gdshaderinc"
