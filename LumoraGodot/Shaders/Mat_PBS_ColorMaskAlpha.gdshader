shader_type spatial;
// Four-way colour pick from a mask texture's channels, over a metallic PBS surface.
//
// Alpha. Loaded when the component's Transparent flag is set.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_back;

#define TRANSPARENT_PASS
#include "res://Shaders/PBS_ColorMask_Core.gdshaderinc"
