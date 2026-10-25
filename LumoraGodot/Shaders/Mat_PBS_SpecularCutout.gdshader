shader_type spatial;
// Specular-workflow PBS, back-face culling (the default), loaded when the blend mode is Cutout. The opaque file with the
// discard compiled in; the opaque files leave it out.
render_mode blend_mix, cull_back;

#define ALPHA_CUTOUT
#include "res://Shaders/PBS_Specular_Core.gdshaderinc"
