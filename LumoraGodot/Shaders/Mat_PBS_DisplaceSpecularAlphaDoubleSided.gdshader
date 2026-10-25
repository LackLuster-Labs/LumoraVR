shader_type spatial;
// Displace PBS, specular workflow, both sides drawn. Blended, premultiplied.
render_mode blend_premul_alpha, depth_prepass_alpha, cull_disabled;

#define TRANSPARENT_PASS
#define PREMULTIPLIED
#define PBS_SPECULAR
#include "res://Shaders/PBS_Displace.gdshaderinc"
