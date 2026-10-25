shader_type spatial;
// Slice PBS, specular workflow, front faces culled. Blended, premultiplied.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_front;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#define PBS_SPECULAR
#include "res://Shaders/PBS_Slice.gdshaderinc"
