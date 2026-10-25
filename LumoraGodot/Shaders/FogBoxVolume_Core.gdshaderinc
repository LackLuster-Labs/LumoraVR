// FogBoxVolume body, shared by the blend variants. Fog filling a unit cube (-0.5..0.5 in object
// space), thickest where the view ray crosses the most of it.
//
// Drawn from the cube's BACK faces with the depth test off, so it still renders with the eye inside
// the box and behind whatever stands in it; the depth buffer is what cuts it short instead. Object
// space measures the ray from where it enters the cube (or the eye, inside it) to the back face or
// the scene, whichever is nearer, in object units. World space fogs everything seen through the box
// by its depth from the eye, in metres, and only uses the cube to skip what is in front of it.
//
// gamma_curve raises the result's colour last; 1 leaves it linear. -xlinka

uniform vec4 base_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 accumulation_color : source_color = vec4(1.0);
uniform vec4 accumulation_color_bottom : source_color = vec4(1.0, 0.0, 0.0, 1.0);
uniform vec4 accumulation_color_top : source_color = vec4(0.0, 1.0, 0.0, 1.0);
uniform float accumulation_rate = 0.5;

uniform float fog_start = 0.0;
uniform float fog_end = 1000000.0;
uniform float fog_density = 0.0;
uniform float gamma_curve = 1.0;

uniform bool world_space = false;
// 0 Constant, 1 VerticalGradient.
uniform int color_mode = 0;
// 0 Unrestricted, 1 SaturateAlpha, 2 SaturateColor.
uniform int saturation_mode = 1;
// 0 Linear, 1 Exponential, 2 ExponentialSquared.
uniform int accumulation_mode = 0;

// Set by the material hook. 0 Opaque, 1 Cutout, 2 Alpha, 3 Transparent, 4 Additive, 5 Multiply.
uniform int blend_mode = 4;

uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;

varying vec3 fog_obj_pos;

vec3 line_plane(vec3 p, vec3 d, vec3 plane_point, vec3 plane_normal) {
	float t = dot(p - plane_point, plane_normal) / dot(d, plane_normal);
	return p - d * t;
}

void keep_nearest(vec3 from, vec3 hit, vec2 on_face, inout vec3 best, inout float best_dist) {
	if (any(greaterThan(abs(on_face), vec2(0.5)))) {
		return;
	}
	float d = distance(from, hit);
	if (d < best_dist) {
		best_dist = d;
		best = hit;
	}
}

// The nearest point to p where the line through it along d meets a face of the cube.
vec3 intersect_unit_cube(vec3 p, vec3 d) {
	vec3 i0 = line_plane(p, d, vec3(-0.5, 0.0, 0.0), vec3(-1.0, 0.0, 0.0));
	vec3 i1 = line_plane(p, d, vec3(0.5, 0.0, 0.0), vec3(1.0, 0.0, 0.0));
	vec3 i2 = line_plane(p, d, vec3(0.0, -0.5, 0.0), vec3(0.0, -1.0, 0.0));
	vec3 i3 = line_plane(p, d, vec3(0.0, 0.5, 0.0), vec3(0.0, 1.0, 0.0));
	vec3 i4 = line_plane(p, d, vec3(0.0, 0.0, -0.5), vec3(0.0, 0.0, -1.0));
	vec3 i5 = line_plane(p, d, vec3(0.0, 0.0, 0.5), vec3(0.0, 0.0, 1.0));

	float best_dist = 65000.0;
	vec3 best = p;
	keep_nearest(p, i0, i0.yz, best, best_dist);
	keep_nearest(p, i1, i1.yz, best, best_dist);
	keep_nearest(p, i2, i2.xz, best, best_dist);
	keep_nearest(p, i3, i3.xz, best, best_dist);
	keep_nearest(p, i4, i4.xy, best, best_dist);
	keep_nearest(p, i5, i5.xy, best, best_dist);
	return best;
}

vec3 clamp_inside_unit_cube(vec3 p, vec3 d) {
	if (all(lessThanEqual(abs(p), vec3(0.5)))) {
		return p;
	}
	return intersect_unit_cube(p, d);
}

void vertex() {
	fog_obj_pos = VERTEX;
}

void fragment() {
	float raw = texture(scene_depth, SCREEN_UV).r;
	vec4 unprojected = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, raw, 1.0);
	float scene_z = -(unprojected.z / unprojected.w);
	float part_z = -VERTEX.z;

	mat4 world_to_object = inverse(MODEL_MATRIX);
	vec3 eye_world = (INV_VIEW_MATRIX * vec4(EYE_OFFSET, 1.0)).xyz;
	vec3 eye_obj = (world_to_object * vec4(eye_world, 1.0)).xyz;

	vec3 origin;
	vec3 start;
	vec3 end;
	bool hidden = false;

	if (!world_space) {
		vec3 ndir = normalize(fog_obj_pos - eye_obj);
		start = clamp_inside_unit_cube(eye_obj, ndir);
		// Both depths lie on the same ray, so their ratio is the ratio of distances along it.
		float end_ratio = min(scene_z / part_z, 1.0);
		end = eye_obj + ndir * distance(eye_obj, fog_obj_pos) * end_ratio;
		origin = eye_obj;
	} else {
		vec3 entry = clamp_inside_unit_cube(eye_obj, normalize(eye_obj - fog_obj_pos));
		float entry_z = -(VIEW_MATRIX * (MODEL_MATRIX * vec4(entry, 1.0))).z;
		hidden = entry_z > scene_z;

		vec3 frag_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 ndir = normalize(frag_world - eye_world);
		start = eye_world;
		// Depth taken as distance, so the fog reads by depth across the view.
		end = eye_world + scene_z * ndir;
		origin = eye_world;
	}

	vec3 to_end = end - origin;
	vec3 to_start = start - origin;
	if (hidden || dot(to_end, to_end) < dot(to_start, to_start)) {
		discard;
	}

	float amount = distance(start, end);
	if (accumulation_mode == 0) {
		amount = max(0.0, min(fog_end, amount) - fog_start);
	} else if (accumulation_mode == 1) {
		amount = 1.0 - 1.0 / exp(amount * fog_density);
	} else {
		float d = amount * fog_density;
		amount = 1.0 - 1.0 / exp(d * d);
	}

	vec4 acc_color = accumulation_color;
	if (color_mode == 1) {
		float start_y;
		float end_y;
		if (!world_space) {
			start_y = start.y;
			end_y = end.y;
		} else {
			vec3 local_start = (world_to_object * vec4(start, 1.0)).xyz;
			vec3 local_end = (world_to_object * vec4(end, 1.0)).xyz;
			start_y = clamp_inside_unit_cube(local_start, normalize(local_start)).y;
			end_y = clamp_inside_unit_cube(local_end, normalize(local_end)).y;
		}
		float avg_y = (start_y + end_y) * 0.5 + 0.5;
		acc_color = mix(accumulation_color_bottom, accumulation_color_top, clamp(avg_y, 0.0, 1.0));
	}

	vec4 result = base_color + amount * acc_color * accumulation_rate;
	if (saturation_mode == 1) {
		result.a = clamp(result.a, 0.0, 1.0);
	} else if (saturation_mode == 2) {
		result = clamp(result, vec4(0.0), vec4(1.0));
	}
	result.rgb = pow(max(result.rgb, vec3(0.0)), vec3(gamma_curve));

	ALBEDO = result.rgb;
#ifdef FOG_ALPHA_ONE
	ALPHA = 1.0;
#else
	ALPHA = blend_mode <= 1 ? 1.0 : result.a;
#endif
}
