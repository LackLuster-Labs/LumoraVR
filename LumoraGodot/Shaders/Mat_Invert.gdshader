shader_type spatial;
// The invert filter in its usual state: normal depth test, writing no depth, no stencil. Every other state is
// generated over the same body (ScreenFilterShader). -xlinka
render_mode unshaded, fog_disabled, blend_mix, cull_disabled, depth_draw_never;

#include "res://Shaders/ScreenFilter_Invert.gdshaderinc"
