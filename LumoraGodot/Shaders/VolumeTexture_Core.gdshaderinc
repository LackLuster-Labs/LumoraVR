// A volume texture drawn by marching through a unit cube (-0.5..0.5 in object space): a scan, a cloud, a field.
// Drawn from the cube's BACK faces with the depth test off, so it still renders with the eye inside the cube;
// the march starts where the ray enters (or at the eye) and stops at the back face or at whatever stands in
// the cube, whichever is nearer.
//
// Each step reads the volume, maps input_range around input_offset to 0..1 (anything outside it reads as
// nothing), raises it to exponent and scales it by gain. display_mode 0 adds every step, 1 adds until the
// running brightness passes accumulation_cutoff, 2 shows the first step bright enough to clear hit_threshold
// and nothing where none is. Up to four planes cut the volume away on the negative side of each, and up to four
// highlight a slab range wide either side of each plane in its own colour, all in the cube's own space.
//
// THE VOLUME IS A 2D STRIP of its depth slices side by side, slice z at columns z * width onwards, the layout
// a package's volume comes in as (PackageRawBitmap). Slices are taken as square, so the strip's width over its
// height is the slice count. Slices are read two at a time and blended, so depth filters as smoothly as the
// other two axes. The volume's columns run along -X: package data is mirrored across X on the way in
// (PackageHandedness) and a texture indexed by position has to be read mirrored with it.
//
// Blending is compile-time; Mat_VolumeTexture is the additive file and the material writes the multiply and
// replace variants over this body. ALPHA is held at one so the blend factors work on colour alone. -xlinka

uniform sampler2D volume_texture : filter_linear, repeat_disable;
uniform bool use_volume_texture = false;

uniform float step_size = 0.01;
uniform float gain = 1.0;
uniform float exponent = 1.0;
uniform float low_clip = 0.0;
uniform float high_clip = 1.0;
uniform float accumulation_cutoff = 1.0;
uniform float hit_threshold = 0.5;
// 0 additive, 1 additive up to the cutoff, 2 first hit over the threshold.
uniform int display_mode = 0;
uniform bool use_alpha_channel = false;

// Planes as (normal, offset): a point's side is dot(point, normal) + offset.
uniform int slice_count = 0;
uniform vec4 slice0 = vec4(0.0);
uniform vec4 slice1 = vec4(0.0);
uniform vec4 slice2 = vec4(0.0);
uniform vec4 slice3 = vec4(0.0);

uniform int highlight_count = 0;
uniform vec4 highlight0 = vec4(0.0);
uniform vec4 highlight1 = vec4(0.0);
uniform vec4 highlight2 = vec4(0.0);
uniform vec4 highlight3 = vec4(0.0);
// One half-width per highlight, in order.
uniform vec4 highlight_ranges = vec4(0.0);
uniform vec4 highlight_color0 : source_color = vec4(1.0);
uniform vec4 highlight_color1 : source_color = vec4(1.0);
uniform vec4 highlight_color2 : source_color = vec4(1.0);
uniform vec4 highlight_color3 : source_color = vec4(1.0);

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;

varying vec3 volume_position;

void vertex() {
	volume_position = VERTEX;
}

// The volume at p in 0..1 on all three axes, from the strip. Each slice is read inside its own texel centres,
// so the filter never borrows a column from the next slice over.
vec4 volume_sample(vec3 p, vec2 texel, float depth) {
	float slice_width = 1.0 / depth;
	float u = clamp(clamp(p.x, 0.0, 1.0) * slice_width, texel.x * 0.5, slice_width - texel.x * 0.5);
	float v = clamp(p.y, texel.y * 0.5, 1.0 - texel.y * 0.5);
	float z = clamp(p.z, 0.0, 1.0) * depth - 0.5;
	float z0 = clamp(floor(z), 0.0, depth - 1.0);
	float z1 = min(z0 + 1.0, depth - 1.0);
	vec4 a = texture(volume_texture, vec2(u + z0 * slice_width, v));
	vec4 b = texture(volume_texture, vec2(u + z1 * slice_width, v));
	return mix(a, b, clamp(z - z0, 0.0, 1.0));
}

// input_range mapped onto 0..1; a channel that falls outside it is nothing at all, not clamped to an end.
float volume_window(float value) {
	float span = high_clip - low_clip;
	float t = (value - low_clip) / (abs(span) < 1e-6 ? 1e-6 : span);
	return (t < 0.0 || t > 1.0) ? 0.0 : t;
}

void fragment() {
	if (!use_volume_texture) {
		discard;
	}

	float raw = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	float scene_z = -(unprojected.z / unprojected.w);
	float part_z = -VERTEX.z;

	vec3 eye_world = (INV_VIEW_MATRIX * vec4(EYE_OFFSET, 1.0)).xyz;
	vec3 eye = (inverse(MODEL_MATRIX) * vec4(eye_world, 1.0)).xyz;
	vec3 dir = normalize(volume_position - eye);

	// Where the ray enters the cube, by the slab test; zero when the eye is already inside.
	vec3 safe = vec3(dir.x < 0.0 ? -1.0 : 1.0, dir.y < 0.0 ? -1.0 : 1.0, dir.z < 0.0 ? -1.0 : 1.0) * max(abs(dir), vec3(1e-6));
	vec3 t_low = (vec3(-0.5) - eye) / safe;
	vec3 t_high = (vec3(0.5) - eye) / safe;
	vec3 t_first = min(t_low, t_high);
	float t_enter = max(max(max(t_first.x, t_first.y), t_first.z), 0.0);
	// Both depths lie on the same ray, so the one over the other is the ratio of distances along it.
	float t_exit = distance(eye, volume_position) * min(scene_z / max(part_z, 1e-6), 1.0);
	if (t_exit < t_enter) {
		discard;
	}

	ivec2 size = textureSize(volume_texture, 0);
	vec2 texel = 1.0 / vec2(max(size, ivec2(1)));
	float depth = max(1.0, floor(float(size.x) / float(max(size.y, 1)) + 0.5));

	float march = max(step_size, 1e-4);
	// A part step at the end still counts as a step.
	int steps = int(ceil(min((t_exit - t_enter) / march, 1024.0)));
	float step_gain = gain;
	if (display_mode != 2) {
		// Each step stands for its share of the path, measured against how the cube is stretched.
		vec3 stretch = vec3(length(MODEL_MATRIX[0].xyz), length(MODEL_MATRIX[1].xyz), length(MODEL_MATRIX[2].xyz));
		step_gain *= length(dir * normalize(max(stretch, vec3(1e-6)))) * march;
	}

	vec4 slices[4] = vec4[4](slice0, slice1, slice2, slice3);
	vec4 highlights[4] = vec4[4](highlight0, highlight1, highlight2, highlight3);
	vec4 highlight_colors[4] = vec4[4](highlight_color0, highlight_color1, highlight_color2, highlight_color3);
	int cuts = clamp(slice_count, 0, 4);
	int marks = clamp(highlight_count, 0, 4);
	float power = max(exponent, 1e-4);

	vec3 pos = eye + dir * t_enter;
	vec3 advance = dir * march;
	vec4 acc = vec4(0.0);
	bool hit = false;
	for (int i = 0; i < steps; i++) {
		vec4 c = volume_sample(vec3(0.5 - pos.x, pos.y + 0.5, pos.z + 0.5), texel, depth);
		if (use_alpha_channel) {
			c = vec4(c.a);
		}
		c = vec4(volume_window(c.r), volume_window(c.g), volume_window(c.b), volume_window(c.a));
		c = pow(c, vec4(power)) * step_gain;

		for (int s = 0; s < cuts; s++) {
			if (dot(pos, slices[s].xyz) + slices[s].w < 0.0) {
				c = vec4(0.0);
			}
		}
		for (int h = 0; h < marks; h++) {
			if (abs(dot(pos, highlights[h].xyz) + highlights[h].w) < highlight_ranges[h]) {
				c *= highlight_colors[h];
			}
		}

		if (display_mode == 2) {
			if ((c.r + c.g + c.b) * 0.3333 >= hit_threshold) {
				acc = c;
				hit = true;
				break;
			}
		} else {
			acc += c;
			if (display_mode == 1 && (acc.r + acc.g + acc.b) * 0.3333 > accumulation_cutoff) {
				break;
			}
		}
		pos += advance;
	}

	if (display_mode == 2 && !hit) {
		discard;
	}
	if (display_mode == 1) {
		acc.rgb /= max(min(1.0, accumulation_cutoff), 1e-6);
	}

	ALBEDO = acc.rgb;
	ALPHA = 1.0;
}
