shader_type spatial;
// Same overlay band as the plain overlay unlit material: additive, never depth-written, never depth-
// tested, both sides drawn. It is meant to sit in the late render queue and stay visible through the
// scene, so ordering is the render priority's job, not the depth buffer's. Other blends, culling and a
// depth write come as generated source over the same body (OverlayFresnelMaterial). -xlinka
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_disabled;

#include "res://Shaders/OverlayFresnel_Core.gdshaderinc"
