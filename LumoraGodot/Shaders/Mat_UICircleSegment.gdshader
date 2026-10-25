shader_type spatial;
// UI circle segment, alpha blended and back culled with no stencil or overlay: the default state. Every other
// state is generated over the same body by UICircleSegmentMaterial. -xlinka
render_mode unshaded, blend_mix, depth_draw_never, cull_back;

#define TRANSPARENT_PASS
#include "res://Shaders/UICircleSegment_Core.gdshaderinc"
