// The end of every generated PBS surface's fragment(), included inside it. Built-ins can only be written
// from fragment() itself, so this is a snippet rather than a function. It reads the pbs_* locals:
//
//   vec3 pbs_albedo, float pbs_alpha, vec3 pbs_emission, float pbs_occlusion, bool pbs_use_occlusion,
//   vec3 pbs_normal (tangent space), bool pbs_use_normal, or vec3 pbs_view_normal under PBS_VIEW_NORMAL,
//   metallic: float pbs_metallic, float pbs_smoothness    specular: vec4 pbs_specular (rgb F0, a smoothness)

#ifdef ZTEST_NEVER
	// A depth test that never passes draws nothing.
	discard;
#endif

#ifdef ALPHA_CLIP
	if (pbs_alpha < alpha_clip) {
		discard;
	}
#endif

#ifdef PBS_SPECULAR
	// Whatever the specular lobe reflects is not left for the diffuse one, and the strongest channel sets
	// that budget. Ambient and probes still run Godot's own path, which reads a dielectric's F0 as
	// 0.16 * SPECULAR^2, so SPECULAR is solved for the same reflectivity to keep them in step.
	float pbs_reflectivity = max(max(pbs_specular.r, pbs_specular.g), pbs_specular.b);
	spec_f0 = pbs_specular.rgb;
	vec3 pbs_diffuse = pbs_albedo * (1.0 - pbs_reflectivity);
	METALLIC = 0.0;
	SPECULAR = clamp(sqrt(pbs_reflectivity / 0.16), 0.0, 1.0);
	ROUGHNESS = clamp(1.0 - pbs_specular.a, 0.0, 1.0);
#else
	float pbs_metal = clamp(pbs_metallic, 0.0, 1.0);
	// What a metallic surface reflects at normal incidence, as a share: 4% for a dielectric up to all of
	// it for a metal. Only the premultiplied alpha below needs it.
	float pbs_reflectivity = mix(0.04, 1.0, pbs_metal);
	vec3 pbs_diffuse = pbs_albedo;
	METALLIC = pbs_metal;
	ROUGHNESS = clamp(1.0 - pbs_smoothness, 0.0, 1.0);
#endif

#if defined(TRANSPARENT_PASS) && defined(PREMULTIPLIED)
	// Premultiplied: the diffuse is scaled by alpha here and blended with ONE, so the reflections on top
	// keep their full strength, and what is behind shows through by the light the surface lets past.
	ALBEDO = pbs_diffuse * pbs_alpha;
	ALPHA = pbs_reflectivity + pbs_alpha * (1.0 - pbs_reflectivity);
#elif defined(TRANSPARENT_PASS)
	ALBEDO = pbs_diffuse;
	ALPHA = pbs_alpha;
#else
	ALBEDO = pbs_diffuse;
#endif

	EMISSION = pbs_emission;

	if (pbs_use_occlusion) {
		AO = pbs_occlusion;
		AO_LIGHT_AFFECT = 0.0;
	}

#ifdef PBS_VIEW_NORMAL
	// A body that works its normal out in view space itself (the triplanar projection) hands it over whole.
	NORMAL = pbs_view_normal;
#else
	// NORMAL arrives already turned toward the viewer on a back face, and the tangent frame does not, so a
	// back face reads its map with the height flipped: lit as the side it is.
	if (pbs_use_normal) {
		NORMAL = normalize(TANGENT * pbs_normal.x + BINORMAL * pbs_normal.y + NORMAL * pbs_normal.z);
	}
#endif

#ifdef DEPTH_OFFSET
	DEPTH = offset_depth(FRAGCOORD.z);
#endif
