shader_type spatial;
// Volume texture, additive: the default state. VolumeTextureMaterial writes the multiply and replace blends over
// the same body. Back faces only, depth test off: see the core. -xlinka
render_mode unshaded, fog_disabled, blend_add, cull_front, depth_draw_never, depth_test_disabled;

#include "res://Shaders/VolumeTexture_Core.gdshaderinc"
