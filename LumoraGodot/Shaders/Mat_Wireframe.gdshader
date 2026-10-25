shader_type spatial;
// Depth-tested wireframe. Mat_WireframeOverlay is the same body with the depth test off; the hook
// swaps between them on DrawOverDepth, the same way the UI unlit family swaps ZTest variants.
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

#include "res://Shaders/Wireframe_Core.gdshaderinc"
