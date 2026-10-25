shader_type spatial;
// The depth projection opaque and writing depth. The blended and depth-write variants are generated over the
// same body (DepthProjectionMaterial). -xlinka
render_mode unshaded, fog_disabled, blend_mix, cull_disabled;

#include "res://Shaders/DepthProjection_Core.gdshaderinc"
