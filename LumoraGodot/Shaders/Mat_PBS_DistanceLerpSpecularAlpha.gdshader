shader_type spatial;
// Distance lerp PBS, specular workflow, back faces culled. Blended, premultiplied.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_back;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#define PBS_SPECULAR
#include "res://Shaders/PBS_DistanceLerp.gdshaderinc"
