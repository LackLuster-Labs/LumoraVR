shader_type spatial;
// UV rect, opaque and back culled with no clip: the default state. Every other state is generated over the same
// body by UVRectMaterial. -xlinka
render_mode unshaded, blend_mix, cull_back;

#include "res://Shaders/UVRect_Core.gdshaderinc"
