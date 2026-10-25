shader_type spatial;
// Frosted backdrop for modal overlays: samples the screen behind it and either blurs
// (gaussian) or quantizes (pixelate) it, then dims toward tint_color. Same UI layer
// conventions as UI_Unlit - depth ignored, ordering by render_priority. - xlinka
render_mode unshaded, blend_mix, depth_draw_never, depth_test_disabled, cull_disabled;

#include "res://Shaders/Blur_Core.gdshaderinc"
