shader_type spatial;
// Metallic-workflow PBS, front faces culled, loaded when the blend mode is Transparent.
//
// Premultiplied: the diffuse fades with alpha and the reflections stay, which is what tells glass from a fade.
// The depth prepass only takes the fragments that are nearly opaque, so clear glass hides nothing behind it.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_front;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#include "res://Shaders/PBS_Metallic_Core.gdshaderinc"
