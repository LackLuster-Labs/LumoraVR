// The atmosphere sky: sunlight scattered through a thin shell of air around a planet, seen from just above its
// surface. Air scatters short wavelengths most (Rayleigh), which blues the sky overhead and reddens a low sun;
// haze scatters forward (Mie), which is the bright halo round the sun. The shell is 2.5% of the planet's
// radius thick and two samples along the ray through it carry the look, with the optical depth from a fitted
// curve rather than a second integral.
//
// sky_tint shifts the wavelengths the air favours: mid grey is the natural sky, warmer greys warm it.
// atmosphere_thickness scales how much air there is, ground_color is what the ground under the horizon
// reflects. Below the horizon the ray meets the ground a short way off; the two branches are blended across a
// narrow band either side of the horizon so the seam between them never shows as a line.
//
// Sky_Atmosphere draws this as the world's sky, Mat_AtmosphereSky as the same sky seen through a surface. Both
// call atmosphere_sky() with the direction looked along, in world space. -xlinka

uniform float atmosphere_thickness = 1.0;
// Read as stored rather than linearised: the wavelength shift steps evenly to the eye in sRGB, which is what
// keeps mid grey at the natural sky.
uniform vec4 sky_tint = vec4(0.5, 0.5, 0.5, 1.0);
uniform vec4 ground_color : source_color = vec4(0.369, 0.349, 0.341, 1.0);
uniform float exposure = 1.3;

// Toward the sun; need not be normalized.
uniform vec3 sun_direction = vec3(-0.577, 0.577, 0.577);
uniform vec4 sun_color : source_color = vec4(1.0);
uniform float sun_intensity = 1.0;
uniform float sun_size = 0.04;
// 0 no disc, 1 a plain disc sun_size across, 2 the haze's own highlight, tightened by sun_size.
uniform int sun_disc = 2;

const float ATMO_PLANET = 1.0;
const float ATMO_OUTER = 1.025;
const float ATMO_EYE_HEIGHT = 0.0001;
const float ATMO_MIE = 0.0010;
const float ATMO_SUN_POWER = 20.0;
const float ATMO_DISC_POWER = 8000.0;
const float ATMO_MAX_SCATTER = 50.0;
const float ATMO_SHELL = 40.0;
const float ATMO_SCALE_DEPTH = 0.25;
const float ATMO_DENSITY_FALLOFF = 160.0;
const float ATMO_HAZE_G = 0.99;
const float ATMO_HORIZON_BAND = 0.02;
const float ATMO_FOUR_PI = 12.566371;

// Optical depth from the edge of the shell down to a point, as a share of the scale height, for a ray at this
// cosine from straight up. A fitted curve; exact enough for a sky and far cheaper than integrating.
float atmo_depth(float cos_angle) {
	float x = 1.0 - cos_angle;
	return ATMO_SCALE_DEPTH * exp(-0.00287 + x * (0.459 + x * (3.83 + x * (-6.80 + x * 5.25))));
}

// Light scattered toward the eye along ray (in_scatter) and the share of sunlight that survives the trip
// (out_scatter), for a ray that climbs out through the shell.
void atmo_up(vec3 ray, vec3 sun, vec3 inv_wave, float rayleigh, out vec3 in_scatter, out vec3 out_scatter) {
	vec3 eye = vec3(0.0, ATMO_PLANET + ATMO_EYE_HEIGHT, 0.0);
	vec3 extinction = inv_wave * rayleigh * ATMO_FOUR_PI + ATMO_MIE * ATMO_FOUR_PI;
	float far = sqrt(ATMO_OUTER * ATMO_OUTER + ray.y * ray.y - 1.0) - ray.y;
	float start_offset = exp(-ATMO_DENSITY_FALLOFF * ATMO_EYE_HEIGHT) * atmo_depth(dot(ray, eye) / eye.y);
	float step_length = far * 0.5;
	vec3 step_ray = ray * step_length;
	vec3 p = eye + step_ray * 0.5;
	vec3 sum = vec3(0.0);
	for (int i = 0; i < 2; i++) {
		float height = length(p);
		float density = exp(ATMO_DENSITY_FALLOFF * (ATMO_PLANET - height));
		float scatter = start_offset + density * (atmo_depth(dot(sun, p) / height) - atmo_depth(dot(ray, p) / height));
		sum += exp(-clamp(scatter, 0.0, ATMO_MAX_SCATTER) * extinction) * (density * step_length * ATMO_SHELL);
		p += step_ray;
	}
	in_scatter = sum * inv_wave * rayleigh * ATMO_SUN_POWER;
	out_scatter = sum * ATMO_MIE * ATMO_SUN_POWER;
}

// The same for a ray that meets the ground. One sample: the path is short and nearly level.
void atmo_down(vec3 ray, vec3 sun, vec3 inv_wave, float rayleigh, out vec3 in_scatter, out vec3 out_scatter) {
	vec3 eye = vec3(0.0, ATMO_PLANET + ATMO_EYE_HEIGHT, 0.0);
	vec3 extinction = inv_wave * rayleigh * ATMO_FOUR_PI + ATMO_MIE * ATMO_FOUR_PI;
	float far = -ATMO_EYE_HEIGHT / min(-0.001, ray.y);
	vec3 ground = eye + far * ray;
	float eye_depth = atmo_depth(dot(-ray, ground));
	float both = atmo_depth(dot(sun, ground)) + eye_depth;
	float eye_offset = exp(-ATMO_EYE_HEIGHT / ATMO_SCALE_DEPTH) * eye_depth;
	float step_length = far * 0.5;
	vec3 p = eye + ray * (step_length * 0.5);
	float density = exp(ATMO_DENSITY_FALLOFF * (ATMO_PLANET - length(p)));
	vec3 survives = exp(-clamp(density * both - eye_offset, 0.0, ATMO_MAX_SCATTER) * extinction);
	in_scatter = survives * (density * step_length * ATMO_SHELL) * (inv_wave * rayleigh + ATMO_MIE) * ATMO_SUN_POWER;
	out_scatter = clamp(survives, 0.0, 1.0);
}

// Rayleigh's phase: as much light scattered forward as back, least to the side.
float atmo_rayleigh_phase(float cos_angle) {
	return 0.75 + 0.75 * cos_angle * cos_angle;
}

// The sun as the eye sees it, times the sunlight left at this ray.
float atmo_disc(vec3 ray, vec3 sun) {
	if (sun_disc == 1) {
		float spot = 1.0 - smoothstep(0.0, sun_size, length(sun - ray));
		return ATMO_DISC_POWER * spot * spot;
	}
	// The haze's forward lobe, raised to a power that narrows as the sun gets smaller.
	float c = dot(sun, ray);
	float g2 = ATMO_HAZE_G * ATMO_HAZE_G;
	float lobe = pow(1.0 + g2 - 2.0 * ATMO_HAZE_G * c, pow(max(sun_size, 0.0), 0.65) * 10.0);
	return 1.5 * ((1.0 - g2) / (2.0 + g2)) * (1.0 + c * c) / max(lobe, 1e-4);
}

vec3 atmosphere_sky(vec3 ray, bool with_disc) {
	ray = normalize(ray);
	vec3 sun = dot(sun_direction, sun_direction) > 1e-8 ? normalize(sun_direction) : vec3(0.0, 1.0, 0.0);

	vec3 wave = mix(vec3(0.50, 0.42, 0.325), vec3(0.80, 0.72, 0.625), vec3(1.0) - sky_tint.rgb);
	vec3 inv_wave = 1.0 / (wave * wave * wave * wave);
	float rayleigh = 0.0025 * pow(max(atmosphere_thickness, 0.0), 2.5);
	float phase = atmo_rayleigh_phase(dot(sun, ray));

	vec3 color = vec3(0.0);
	float lift = smoothstep(-ATMO_HORIZON_BAND, ATMO_HORIZON_BAND, ray.y);

	if (ray.y < ATMO_HORIZON_BAND) {
		vec3 in_scatter;
		vec3 out_scatter;
		vec3 down = normalize(vec3(ray.x, min(ray.y, 0.0), ray.z));
		atmo_down(down, sun, inv_wave, rayleigh, in_scatter, out_scatter);
		vec3 sky_side = in_scatter * phase;
		vec3 ground_side = in_scatter + ground_color.rgb * out_scatter;
		color += mix(sky_side, ground_side, clamp(-ray.y / ATMO_HORIZON_BAND, 0.0, 1.0)) * (1.0 - lift);
	}

	if (ray.y > -ATMO_HORIZON_BAND) {
		vec3 in_scatter;
		vec3 out_scatter;
		vec3 up = normalize(vec3(ray.x, max(ray.y, 0.0), ray.z));
		atmo_up(up, sun, inv_wave, rayleigh, in_scatter, out_scatter);
		color += in_scatter * phase * lift;
		if (with_disc && sun_disc != 0 && ray.y > 0.0) {
			color += atmo_disc(ray, sun) * out_scatter * sun_color.rgb * sun_intensity;
		}
	}

	return color * exposure;
}
