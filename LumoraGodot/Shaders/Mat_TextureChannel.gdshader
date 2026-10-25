shader_type spatial;
// One channel of a texture shown as grey: texture_channel 0 red, 1 green, 2 blue, 3 alpha. Any other value
// shows the texture as it is. Colour channels are shown as they are stored; alpha, which is never gamma
// encoded, reads a little brighter for it. Opaque and unlit. -xlinka
render_mode unshaded, blend_mix, cull_back;

uniform sampler2D channel_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_channel_texture = false;
uniform float texture_channel = 0.0;

void fragment() {
	vec4 c = use_channel_texture ? texture(channel_texture, UV) : vec4(1.0);
	if (texture_channel == 0.0) {
		c = vec4(c.rrr, 1.0);
	} else if (texture_channel == 1.0) {
		c = vec4(c.ggg, 1.0);
	} else if (texture_channel == 2.0) {
		c = vec4(c.bbb, 1.0);
	} else if (texture_channel == 3.0) {
		c = vec4(c.aaa, 1.0);
	}
	ALBEDO = c.rgb;
}
