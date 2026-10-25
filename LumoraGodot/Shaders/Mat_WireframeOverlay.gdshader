shader_type spatial;
// Wireframe that ignores the depth buffer, so the wires stay visible through solid geometry. Same body
// as Mat_Wireframe; the hook picks this one when DrawOverDepth is set.
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled;

#include "res://Shaders/Wireframe_Core.gdshaderinc"
