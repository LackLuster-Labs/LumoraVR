// Direct lighting of the specular workflow, shared by every material that takes its reflectance as a
// colour: PBS_Specular and the specular members of the slice, stencil, displace and distance lerp families.
//
// Ported from Unity's built-in Standard (Specular setup) BRDF, MIT licensed, Copyright (c) 2016 Unity
// Technologies, UnityStandardBRDF.cginc. The including shader writes spec_f0 in fragment() before this runs.

// The one thing Godot's light stage cannot carry for us: a per-channel F0.
varying vec3 spec_f0;

// Cook-Torrance with a GGX distribution, a Smith-joint visibility approximation and a Schlick fresnel
// that runs on the COLOURED F0, which is the whole reason this cannot be left to Godot's built-in light path.
// -xlinka
void light() {
	vec3 N = normalize(NORMAL);
	vec3 V = normalize(VIEW);
	vec3 L = normalize(LIGHT);
	vec3 H = normalize(L + V);

	// One branch around the whole body rather than an early out: 'return' is rejected outright in a
	// light() processor.
	float n_dot_l = clamp(dot(N, L), 0.0, 1.0);
	if (n_dot_l > 0.0) {
		float n_dot_v = abs(dot(N, V)) + 1e-5;
		float n_dot_h = clamp(dot(N, H), 0.0, 1.0);
		float l_dot_h = clamp(dot(L, H), 0.0, 1.0);

		// Clamped away from a perfect mirror: at zero roughness the lobe collapses to a subpixel spike
		// that aliases into fireflies on anything animated.
		float rough = clamp(ROUGHNESS, 0.045, 1.0);
		float a = rough * rough;
		float a2 = a * a;

		float denom = (n_dot_h * a2 - n_dot_h) * n_dot_h + 1.0;
		float d_term = a2 / max(PI * denom * denom, 1e-7);

		float lambda_v = n_dot_l * (n_dot_v * (1.0 - a) + a);
		float lambda_l = n_dot_v * (n_dot_l * (1.0 - a) + a);
		float vis = 0.5 / max(lambda_v + lambda_l, 1e-5);

		vec3 fresnel = spec_f0 + (vec3(1.0) - spec_f0) * pow(1.0 - l_dot_h, 5.0);

		vec3 radiance = LIGHT_COLOR * ATTENUATION * n_dot_l;

		// DIFFUSE_LIGHT is multiplied by ALBEDO when the pass resolves, so the lambert 1/PI goes here
		// and the albedo does not.
		DIFFUSE_LIGHT += radiance / PI;
		SPECULAR_LIGHT += d_term * vis * fresnel * radiance;
	}
}
