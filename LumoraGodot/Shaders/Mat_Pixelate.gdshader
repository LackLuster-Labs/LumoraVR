shader_type spatial;
// The pixelate filter in its usual state: opaque, so writing depth, normal depth test, no stencil. Every
// other state is generated over the same body (ScreenFilterShader). -xlinka
render_mode unshaded, fog_disabled, blend_mix, cull_disabled, depth_draw_always;

#include "res://Shaders/ScreenFilter_Pixelate.gdshaderinc"
