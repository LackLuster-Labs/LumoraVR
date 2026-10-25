// Shared body of the blur: samples the screen behind the surface and either blurs it (gaussian), quantizes it
// (pixelate) or averages a scatter of taps across a spread, then dims toward tint_color.
//
// Blur.gdshader is the UI backdrop's state: depth ignored, both sides, nothing written but colour. A blur
// standing in the world tests and writes depth, culls and blends as its material says, through generated
// source over this body (BlurMaterial). Defines it can set:
//   BLUR_SCATTER  compiles the scatter, which reads the scene's depth; the backdrop never needs either
//   BLUR_NEVER    draws nothing, for a depth test that never passes
// -xlinka

uniform sampler2D screen_tex : hint_screen_texture, filter_linear, repeat_disable;
#ifdef BLUR_SCATTER
uniform sampler2D scene_depth : hint_depth_texture, filter_nearest;
#endif

// 0 = Gaussian, 1 = Pixelate, 2 = Scatter.
uniform int blur_mode = 0;
// Gaussian: per-tap sample spread in px. Pixelate: block size in px.
uniform float blur_radius : hint_range(0.0, 64.0) = 6.0;
// Dim/wash applied over the blurred result; alpha = how far to mix toward the color.
uniform vec4 tint_color : source_color = vec4(0.03, 0.03, 0.06, 0.55);
// Overall opacity of the backdrop. < 1.0 lets the content behind show through (dimmed) - important
// because the dashboard UI is transparent and is NOT in the screen copy the blur samples, so the
// frost alone would just hide it. Keeping this translucent makes the panel read as a modal. - xlinka
uniform float opacity : hint_range(0.0, 1.0) = 0.85;

uniform bool rect_clip = false;
uniform vec4 rect = vec4(0.0);

// SCATTER. iterations taps across spread, a fraction of the screen per axis: on a ring at its edge, or
// filling the disc. The spread texture scales it per pixel from red and green, and it shrinks to nothing as
// the surface meets what is behind it, over depth_fade_divisor metres.
uniform int iterations = 10;
uniform vec2 spread = vec2(0.005);
uniform sampler2D spread_magnitude_texture : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_spread_magnitude_texture = false;
uniform vec2 spread_texture_scale = vec2(1.0);
uniform vec2 spread_texture_offset = vec2(0.0);
uniform bool use_poisson_disc = true;
uniform float depth_fade_divisor = 0.1;

// Bends the screen read by the surface's view-space normal, more for nearer surfaces.
uniform bool use_refraction = false;
uniform float refraction_strength = 0.05;
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform vec2 normal_texture_scale = vec2(1.0);
uniform vec2 normal_texture_offset = vec2(0.0);

varying vec2 local_pos;

float rect_alpha(vec2 pos, vec4 clip_rect) {
	vec2 edge = max(fwidth(pos), vec2(0.0001));
	float left = smoothstep(clip_rect.x - edge.x, clip_rect.x + edge.x, pos.x);
	float bottom = smoothstep(clip_rect.y - edge.y, clip_rect.y + edge.y, pos.y);
	float right = 1.0 - smoothstep(clip_rect.z - edge.x, clip_rect.z + edge.x, pos.x);
	float top = 1.0 - smoothstep(clip_rect.w - edge.y, clip_rect.w + edge.y, pos.y);
	return clamp(left * bottom * right * top, 0.0, 1.0);
}

// 5x5 weighted gaussian in a single pass. Cheap enough for a full-screen backdrop that
// only renders while a dialog is open. blur_radius scales the tap spacing. - xlinka
vec3 gaussian(vec2 uv, vec2 px) {
	vec3 sum = vec3(0.0);
	float total = 0.0;
	for (int x = -2; x <= 2; x++) {
		for (int y = -2; y <= 2; y++) {
			vec2 off = vec2(float(x), float(y)) * px * blur_radius;
			float w = exp(-float(x * x + y * y) * 0.5);
			sum += texture(screen_tex, uv + off).rgb * w;
			total += w;
		}
	}
	return sum / max(total, 0.0001);
}

// Quantize the screen UV into blur_radius-sized blocks and sample the block center.
vec3 pixelate(vec2 uv, vec2 px) {
	vec2 block = px * max(blur_radius, 1.0);
	vec2 snapped = (floor(uv / block) + 0.5) * block;
	return texture(screen_tex, snapped).rgb;
}

#ifdef BLUR_SCATTER
// The disc fills by the golden angle, each tap at the radius that keeps equal area per tap, out to the radius
// of a disc as large as the square of side two, so a disc and a square scatter reach about as far.
vec3 scatter(vec2 uv, vec2 reach) {
	int taps = clamp(iterations, 1, 128);
	vec3 sum = vec3(0.0);
	for (int i = 0; i < taps; i++) {
		vec2 dir;
		if (use_poisson_disc) {
			float a = float(i) * 2.39996323;
			dir = vec2(cos(a), sin(a)) * sqrt((float(i) + 0.5) / float(taps)) * 1.12837917;
		} else {
			float a = TAU * float(i) / float(taps);
			dir = vec2(-cos(a), sin(a));
		}
		sum += texture(screen_tex, uv + dir * reach).rgb;
	}
	return sum / float(taps);
}
#endif

void vertex() {
	local_pos = VERTEX.xy;
}

void fragment() {
#ifdef BLUR_NEVER
	discard;
#endif
	vec2 px = 1.0 / VIEWPORT_SIZE;
	vec3 base;
#ifdef BLUR_SCATTER
	if (blur_mode == 2) {
		vec2 uv = SCREEN_UV;
		float frag_dist = max(-VERTEX.z, 0.0001);
		if (use_refraction) {
			vec3 n = normalize(NORMAL);
			if (use_normal_map) {
				vec3 tn = texture(normal_map, UV * normal_texture_scale + normal_texture_offset).xyz * 2.0 - 1.0;
				n = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
			}
			uv -= n.xy / frag_dist * refraction_strength;
		}
		vec2 reach = spread;
		if (use_spread_magnitude_texture) {
			reach *= texture(spread_magnitude_texture, UV * spread_texture_scale + spread_texture_offset).rg;
		}
		float raw = texture(scene_depth, uv).r;
		vec4 unprojected = INV_PROJECTION_MATRIX * vec4(uv * 2.0 - 1.0, raw, 1.0);
		float scene_dist = -(unprojected.z / unprojected.w);
		reach *= clamp((scene_dist - frag_dist) / max(depth_fade_divisor, 0.000001), 0.0, 1.0);
		base = scatter(uv, reach);
	} else {
		base = (blur_mode == 1) ? pixelate(SCREEN_UV, px) : gaussian(SCREEN_UV, px);
	}
#else
	base = (blur_mode == 1) ? pixelate(SCREEN_UV, px) : gaussian(SCREEN_UV, px);
#endif
	vec3 col = mix(base, tint_color.rgb, tint_color.a);

	ALBEDO = col;
	ALPHA = opacity * (rect_clip ? rect_alpha(local_pos, rect) : 1.0);
}
