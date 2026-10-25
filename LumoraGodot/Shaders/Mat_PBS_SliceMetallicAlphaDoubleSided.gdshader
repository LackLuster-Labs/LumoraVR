shader_type spatial;
// Slice PBS, metallic workflow, both sides drawn. Blended, premultiplied.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_disabled;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#include "res://Shaders/PBS_Slice.gdshaderinc"
