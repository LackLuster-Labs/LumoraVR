// Projection360 body, shared by every variant. A panorama or a cubemap picked by DIRECTION rather
// than by mesh UV: the direction to the eye in object space (View) or world space (WorldspaceView),
// the surface normal (Normal), or a pinhole camera laid across the mesh UVs (Perspective).
//
// Directions are worked in the left-handed frame the content was authored in, X mirrored on the way
// in, so the panorama mapping and the angle offsets land where the author put them. A cubemap is
// sampled in ours, the way the sky samples one. Equirect UVs are worked v-up, as authored, and turned
// to our top-origin rows only on the fetch.
//
// Culling is a uniform here, not a render_mode: the variants all draw both faces and the culled one
// is discarded, which keeps the file count to blend and depth write. -xlinka

const float P360_PI = 3.14159265359;
const float P360_TAU = 6.28318530718;

// 0 View, 1 WorldspaceView, 2 Normal, 3 Perspective.
uniform int projection_mode = 0;
// xy the angular span, zw the angle offset, all radians.
uniform vec4 field_of_view = vec4(6.28318530718, 3.14159265359, 0.0, 0.0);
uniform vec4 perspective_fov = vec4(0.785398, 0.785398, 0.0, 0.0);

uniform sampler2D main_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_main_texture = false;
uniform sampler2D main_texture_right : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_main_texture_right = false;
uniform sampler2D second_texture : source_color, filter_linear_mipmap, repeat_enable;
uniform bool use_second_texture = false;

uniform samplerCube main_cube : source_color, hint_default_black, filter_linear_mipmap;
uniform samplerCube second_cube : source_color, hint_default_black, filter_linear_mipmap;
uniform bool use_cubemap = false;
uniform bool use_second_cube = false;
uniform bool use_cube_lod = false;
uniform float cube_lod = 0.0;

// On with a secondary picture or any lerp at all; an absent secondary lerps toward clear black.
uniform bool secondary_blend = false;
uniform float texture_lerp = 0.0;

uniform vec4 main_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);
uniform bool stereo_texture_transform = false;
uniform vec4 right_eye_transform = vec4(1.0, 1.0, 0.0, 0.0);

// 0 Clip, 1 Color, 2 Clamp. Only the equirect path can fall outside.
uniform int outside_mode = 0;
uniform vec4 outside_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);

uniform vec4 tint : source_color = vec4(1.0);
uniform float exposure = 1.0;
uniform float gamma = 1.0;
uniform bool clamp_intensity = false;
uniform float max_intensity = 4.0;

// 0 Direct multiplies by the texture, 1 Lerp picks between tint0 and tint1 by its red channel.
uniform sampler2D tint_texture : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_tint_texture = false;
uniform int tint_texture_mode = 0;
uniform vec4 tint_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 tint0 : source_color = vec4(0.0);
uniform vec4 tint1 : source_color = vec4(0.0);

// Turns the lookup direction by the offset texture's red/green, times the mask, times the magnitude.
uniform sampler2D offset_texture : filter_linear_mipmap, repeat_enable;
uniform bool use_offset_texture = false;
uniform sampler2D offset_mask_texture : hint_default_white, filter_linear_mipmap, repeat_enable;
uniform bool use_offset_mask_texture = false;
uniform vec4 offset_texture_transform = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec2 offset_magnitude = vec2(0.0174533);

// Set by the material hook. 0 culls back faces, 1 front faces, 2 neither.
uniform int cull_mode = 1;

varying vec3 p360_obj_pos;
varying vec3 p360_obj_normal;
varying vec3 p360_obj_eye;

// Pitch about X by rotate.y, then yaw about Y by rotate.x.
vec3 p360_rotate(vec3 d, vec2 rotate) {
	float s = sin(rotate.y);
	float c = cos(rotate.y);
	d = vec3(d.x, c * d.y - s * d.z, s * d.y + c * d.z);
	s = sin(rotate.x);
	c = cos(rotate.x);
	return vec3(c * d.x + s * d.z, d.y, -s * d.x + c * d.z);
}

// Direction to v-up equirect UV over the authored span. Inside the span lands in 0..1.
vec2 p360_uv(vec3 d) {
	vec2 angle = vec2(atan(d.x, d.z), acos(clamp(d.y, -1.0, 1.0)) - P360_PI * 0.5);
	angle += field_of_view.xy * 0.5;
	angle += field_of_view.zw;
	angle = mod(angle, vec2(P360_TAU, P360_PI));
	return angle / field_of_view.xy;
}

vec2 p360_rows(vec2 uv) {
	return vec2(uv.x, 1.0 - uv.y);
}

void vertex() {
	p360_obj_pos = VERTEX;
	p360_obj_normal = NORMAL;
	p360_obj_eye = (inverse(MODEL_MATRIX) * (INV_VIEW_MATRIX * vec4(EYE_OFFSET, 1.0))).xyz;
}

void fragment() {
	if ((cull_mode == 0 && !FRONT_FACING) || (cull_mode == 1 && FRONT_FACING)) {
		discard;
	}

	vec3 dir;
	if (projection_mode == 3) {
		// The authored y flip and our top-origin UV rows cancel.
		vec2 plane = (UV - 0.5) * 2.0;
		dir = normalize(vec3(tan(perspective_fov.xy * 0.5) * plane, 1.0));
		dir = p360_rotate(dir, perspective_fov.zw);
	} else {
		vec3 d;
		if (projection_mode == 1) {
			vec3 world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
			vec3 world_eye = (INV_VIEW_MATRIX * vec4(EYE_OFFSET, 1.0)).xyz;
			d = world_eye - world_pos;
		} else if (projection_mode == 2) {
			d = -p360_obj_normal;
		} else {
			d = p360_obj_eye - p360_obj_pos;
		}
		dir = normalize(vec3(-d.x, d.y, d.z));
	}

	if (use_offset_texture) {
		vec2 offset_uv = p360_uv(dir);
		vec2 offset = textureLod(offset_texture, p360_rows(offset_uv * offset_texture_transform.xy + offset_texture_transform.zw), 0.0).rg;
		vec2 mask = use_offset_mask_texture ? textureLod(offset_mask_texture, p360_rows(offset_uv), 0.0).rg : vec2(1.0);
		dir = p360_rotate(dir, (offset * 2.0 - 1.0) * mask * offset_magnitude);
	}

	vec4 c = vec4(0.0, 0.0, 0.0, 1.0);
	bool show_outside = false;

	if (use_cubemap) {
		vec3 cube_dir = vec3(dir.x, -dir.y, -dir.z);
		c = use_cube_lod ? textureLod(main_cube, cube_dir, cube_lod) : texture(main_cube, cube_dir);
		if (secondary_blend) {
			vec4 sc = vec4(0.0);
			if (use_second_cube) {
				sc = use_cube_lod ? textureLod(second_cube, cube_dir, cube_lod) : texture(second_cube, cube_dir);
			}
			c = mix(c, sc, texture_lerp);
		}
	} else {
		vec2 uv = p360_uv(dir);
		bool outside = uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0;
		if (outside && outside_mode == 0) {
			discard;
		}
		show_outside = outside && outside_mode == 1;
		if (outside_mode == 2) {
			uv = clamp(uv, vec2(0.0), vec2(1.0));
		}

		vec4 st = stereo_texture_transform && VIEW_INDEX == VIEW_RIGHT ? right_eye_transform : main_texture_transform;
		uv = uv * st.xy + st.zw;
		vec2 rows = p360_rows(uv);

		// A picture that carries its own right-eye frame shows it to the right eye.
		if (use_main_texture_right && VIEW_INDEX == VIEW_RIGHT) {
			c = textureLod(main_texture_right, rows, 0.0);
		} else if (use_main_texture) {
			c = textureLod(main_texture, rows, 0.0);
		}

		if (secondary_blend) {
			vec4 sc = use_second_texture ? textureLod(second_texture, rows, 0.0) : vec4(0.0);
			c = mix(c, sc, texture_lerp);
		}

		if (use_tint_texture) {
			if (tint_texture_mode == 0) {
				c *= textureLod(tint_texture, rows, 0.0);
			} else {
				// The offset is read w then z, as content was authored against.
				vec2 tint_uv = uv * tint_texture_transform.xy + tint_texture_transform.wz;
				float l = textureLod(tint_texture, p360_rows(tint_uv), 0.0).r;
				c *= mix(tint0, tint1, l);
			}
		}
	}

	// Fades out what is closer than 15 cm, gone by 5 cm, so a sphere around the head does not clip
	// through the eye.
	float fade = clamp(clamp(-VERTEX.z - 0.05, 0.0, 0.1) * 10.0, 0.0, 1.0);
	vec4 t = tint;
	t.a *= fade;
	c = vec4(pow(max(c.rgb, vec3(0.0)), vec3(gamma)) * exposure, c.a) * t;

	if (clamp_intensity) {
		float m = max(c.r, max(c.g, c.b));
		if (m > max_intensity) {
			c.rgb *= max_intensity / m;
		}
	}

	if (show_outside) {
		c = outside_color;
	}

	ALBEDO = c.rgb;
#ifdef P360_ALPHA
	ALPHA = c.a;
#endif
#ifdef P360_ALPHA_ONE
	ALPHA = 1.0;
#endif
}
