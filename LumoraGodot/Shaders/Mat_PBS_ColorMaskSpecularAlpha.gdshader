shader_type spatial;
// Four-way colour pick from a mask texture's channels, over a specular PBS surface. Blended, premultiplied:
// loaded when the component's Transparent flag is set.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_back;

#define PBS_SPECULAR
#define TRANSPARENT_PASS
#include "res://Shaders/PBS_ColorMask_Core.gdshaderinc"
