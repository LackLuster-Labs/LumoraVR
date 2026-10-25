shader_type spatial;
// Slice PBS, metallic workflow, front faces culled. Blended, premultiplied.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_front;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#include "res://Shaders/PBS_Slice.gdshaderinc"
