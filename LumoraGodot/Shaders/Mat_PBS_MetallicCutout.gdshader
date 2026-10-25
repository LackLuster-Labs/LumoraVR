shader_type spatial;
// Metallic-workflow PBS, back-face culling, loaded when the blend mode is Cutout.
//
// The opaque file with the cutout discard compiled in. The opaque files leave it out, since a discard in a
// shader at all costs it Godot's shared depth and shadow material. -xlinka
render_mode blend_mix, cull_back;

#define ALPHA_CUTOUT
#include "res://Shaders/PBS_Metallic_Core.gdshaderinc"
