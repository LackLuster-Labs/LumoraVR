shader_type spatial;
// Unlit distance lerp, opaque and back culled: the default state. Every other blend, depth and cull state is
// generated over the same body by UnlitDistanceLerpMaterial. -xlinka
render_mode unshaded, blend_mix, cull_back;

#include "res://Shaders/UnlitDistanceLerp_Core.gdshaderinc"
