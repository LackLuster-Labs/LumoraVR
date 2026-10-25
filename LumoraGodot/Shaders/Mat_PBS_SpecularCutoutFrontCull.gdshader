shader_type spatial;
// Specular-workflow PBS, front faces culled, loaded when the blend mode is Cutout. The opaque file with the
// discard compiled in; the opaque files leave it out.
render_mode blend_mix, cull_front;

#define ALPHA_CUTOUT
#include "res://Shaders/PBS_Specular_Core.gdshaderinc"
