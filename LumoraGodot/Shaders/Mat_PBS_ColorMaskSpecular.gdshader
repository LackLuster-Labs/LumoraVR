shader_type spatial;
// Four-way colour pick from a mask texture's channels, over a specular PBS surface. Opaque: writes no ALPHA.
render_mode blend_mix, cull_back, depth_draw_opaque;

#define PBS_SPECULAR
#include "res://Shaders/PBS_ColorMask_Core.gdshaderinc"
