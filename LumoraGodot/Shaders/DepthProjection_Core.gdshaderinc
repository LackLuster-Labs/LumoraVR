// A depth camera's picture rebuilt in 3D. The mesh is only a grid of UVs: each vertex is pushed out along its
// own ray, as far as the depth texture says, and coloured from the colour texture at the same UV. A grid a few
// hundred points a side makes a skin over the scene the camera saw.
//
// Depth reads as 1 minus the red channel, or, encoded as a spectrum, as the hue: red nearest, round through the
// colours to the far end, grey furthest. Each vertex averages its own sample with four neighbours
// discard_offset away, eases it by log(d + 1) / log(depth_to + 1) * d, and spreads that from depth_from to
// depth_to metres. The rays fan out as tan(field_of_view) across the whole grid, which is how the package's
// content was tuned, so 60 degrees reads somewhat wider than a 60 degree camera.
//
// Where the five samples disagree by more than discard_threshold the grid tears: those triangles span a
// silhouette and would be a sheet stretched between the foreground and what is behind it. near_clip and
// far_clip cut by the averaged depth, 0 to 1, before the spread.
//
// u runs across the picture and v down it, as every picture's UVs do here. The rays look down +Z with the
// picture's right toward -X, so it reads the right way round from behind the camera. -xlinka

uniform sampler2D color_texture : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_color_texture = false;
uniform sampler2D depth_texture : hint_default_black, filter_linear, repeat_enable;
uniform bool use_depth_texture = false;
uniform vec4 color_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 depth_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);

// 0 grey, 1 hue.
uniform int depth_encoding = 0;
uniform float depth_from = 0.0;
uniform float depth_to = 1.0;
// Radians, across and down.
uniform vec2 field_of_view = vec2(1.0471976, 0.5890486);
uniform float near_clip = 0.0;
uniform float far_clip = 1.0;
uniform float discard_threshold = 0.01;
uniform float discard_offset = 0.01;

varying float dp_depth;
varying float dp_tear;

float dp_sample(vec2 uv) {
	vec3 c = use_depth_texture
		? textureLod(depth_texture, uv * depth_texture_transform.xy + depth_texture_transform.zw, 0.0).rgb
		: vec3(0.0);
	if (depth_encoding != 1) {
		return 1.0 - c.r;
	}

	float high = max(max(c.r, c.g), c.b);
	float low = min(min(c.r, c.g), c.b);
	float chroma = high - low;
	if (chroma == 0.0) {
		return 1.0;
	}
	// The red sector runs from -1/6 to 1/6 and is not wrapped round to the top.
	if (high == c.r) {
		return (c.g - c.b) / chroma / 6.0;
	}
	if (high == c.g) {
		return ((c.b - c.r) / chroma + 2.0) / 6.0;
	}
	return ((c.r - c.g) / chroma + 4.0) / 6.0;
}

void vertex() {
	float centre = dp_sample(UV);
	vec4 around = vec4(
		dp_sample(UV + vec2(discard_offset, 0.0)),
		dp_sample(UV + vec2(0.0, discard_offset)),
		dp_sample(UV - vec2(discard_offset, 0.0)),
		dp_sample(UV - vec2(0.0, discard_offset)));
	vec4 gap = abs(around - vec4(centre));
	dp_tear = max(max(gap.x, gap.y), max(gap.z, gap.w));

	float d = (centre + around.x + around.y + around.z + around.w) / 5.0;
	dp_depth = d;
	float ease = log(depth_to + 1.0);
	d = log(d + 1.0) / (abs(ease) < 0.000001 ? 0.000001 : ease) * d;
	d = (depth_to - depth_from) * d + depth_from;

	vec2 ray = (vec2(0.5) - UV) * tan(field_of_view);
	VERTEX = vec3(ray, 1.0) * d;
}

void fragment() {
	if (dp_tear > discard_threshold || dp_depth < near_clip || dp_depth > far_clip) {
		discard;
	}

	vec4 c = use_color_texture
		? texture(color_texture, UV * color_texture_transform.xy + color_texture_transform.zw)
		: vec4(1.0);
	ALBEDO = c.rgb;
#ifdef DEPTH_PROJECTION_ALPHA
	ALPHA = c.a;
#endif
#ifdef DEPTH_PROJECTION_ALPHA_ONE
	ALPHA = 1.0;
#endif
}
