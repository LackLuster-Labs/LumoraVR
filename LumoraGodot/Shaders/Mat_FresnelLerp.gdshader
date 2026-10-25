shader_type spatial;
// Alpha with a depth pre-pass: a fully opaque set of colours still sorts and occludes correctly,
// while a translucent one still blends. Every other state is generated over the same body
// (FresnelLerpMaterial). -xlinka
render_mode unshaded, blend_mix, depth_prepass_alpha, cull_back;

#define TRANSPARENT_PASS
#include "res://Shaders/FresnelLerp_Core.gdshaderinc"
