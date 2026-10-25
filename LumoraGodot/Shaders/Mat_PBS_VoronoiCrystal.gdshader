shader_type spatial;
// PBS crystal cells: the UV square cut into noise_scale Voronoi cells that tile at its edges, each cell a flat
// facet with a colour, an emission and a smoothness of its own, and a seam of edge_color edge_thickness wide
// where two cells meet. The distance to the nearest cell point and to the second nearest give the seam: it is
// where the two are within edge_thickness of each other. noise_animation_offset swings every cell point round
// its own small circle, so animating it makes the cells crawl.
//
// A cell's colour and emission are read from the two gradient textures at a spot that point picks, its
// smoothness from the smoothness gradient at the point's own hash; unset gradients read as white. The cell's
// normal is the normal map moved by the cell's hash, so each facet tilts its own way; the seam leans its normal
// across itself by edge_normal_strength, along the true slope of the seam distance. Opaque, metallic.
//
// The cells are laid out with V counted up from the bottom, and the gradients are looked up by that same
// reckoning, the way a package's crystal works them: imported crystals show the very cells they had, and the
// pattern is random anyway for anything made here. -xlinka
render_mode blend_mix, cull_back;

#include "res://Shaders/PBS_Surface_Common.gdshaderinc"

uniform vec2 noise_scale = vec2(10.0);
uniform float noise_animation_offset = 0.0;

uniform vec4 cell_color_tint : source_color = vec4(1.0);
uniform sampler2D color_gradient_texture : source_color, filter_linear, repeat_disable;
uniform bool use_color_gradient_texture = false;
uniform vec4 emission_color : source_color = vec4(0.0, 0.0, 0.0, 0.0);
uniform sampler2D emission_gradient_texture : source_color, filter_linear, repeat_disable;
uniform bool use_emission_gradient_texture = false;
uniform sampler2D smoothness_gradient_texture : filter_linear, repeat_disable;
uniform bool use_smoothness_gradient_texture = false;
uniform float cell_smoothness = 0.5;
uniform float cell_metallic = 0.1;

uniform sampler2D normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_strength = 1.0;
uniform vec2 normal_texture_scale = vec2(1.0);
uniform vec2 normal_texture_offset = vec2(0.0);

uniform float edge_thickness = 0.1;
uniform vec4 edge_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform vec4 edge_emission : source_color = vec4(0.0, 0.0, 0.0, 0.0);
uniform float edge_smoothness = 0.8;
uniform float edge_metallic = 0.1;
uniform float edge_normal_strength = 0.5;

// A cell's point, fixed per cell. The same hash a package's crystal uses, so its cells land where they did.
vec2 crystal_hash(vec2 cell) {
	return fract(sin(vec2(dot(cell, vec2(127.1, 311.7)), dot(cell, vec2(269.5, 183.3)))) * 43758.5453);
}

void fragment() {
	vec2 scale = max(abs(noise_scale), vec2(1e-4));
	vec2 cell_uv = vec2(UV.x, 1.0 - UV.y);
	vec2 p = cell_uv * noise_scale;
	vec2 cell = floor(p);
	vec2 inside = fract(p);

	float nearest = 2.0;
	float second = 2.0;
	vec2 nearest_hash = vec2(0.0);
	vec2 to_nearest = vec2(0.0);
	vec2 to_second = vec2(0.0);
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			vec2 neighbour = vec2(float(x), float(y));
			// Wrapped to the scale, so the cells tile across the UV square's edges.
			vec2 hash = crystal_hash(mod(cell + neighbour, scale));
			vec2 site = 0.5 + 0.5 * sin(noise_animation_offset + 6.2831 * hash);
			vec2 offset = neighbour + site - inside;
			float d = length(offset);
			if (d < nearest) {
				second = nearest;
				to_second = to_nearest;
				nearest = d;
				to_nearest = offset;
				nearest_hash = hash;
			} else if (d < second) {
				second = d;
				to_second = offset;
			}
		}
	}

	float seam = second - nearest;
	float aa = fwidth(seam);
	float in_cell = smoothstep(edge_thickness - aa, edge_thickness, seam);

	// The seam distance climbs away from the seam toward each cell's middle; its slope, worked from the two
	// nearest points rather than from screen derivatives, leans the seam's normal. The cells' V runs up, as the
	// tangent frame's y does, so the slope goes over as it is.
	vec2 slope = (to_nearest / max(nearest, 1e-5) - to_second / max(second, 1e-5)) * noise_scale;
	vec2 lean = dot(slope, slope) > 1e-10 ? normalize(slope) * edge_normal_strength : vec2(0.0);
	vec3 seam_normal = normalize(vec3(lean, 1.0));

	vec3 cell_normal = vec3(0.0, 0.0, 1.0);
	if (use_normal_map) {
		vec2 nuv = UV * normal_texture_scale + normal_texture_offset;
		// Read with the derivatives of the unshifted UV, so the jump between cells does not pick a tiny mip.
		vec2 shift = vec2(nearest_hash.x, -nearest_hash.y);
		cell_normal = pbs_unpack_normal(textureGrad(normal_map, nuv + shift, dFdx(nuv), dFdy(nuv)), normal_strength);
	}

	// Gradient lookups by the cells' V-up reckoning, onto pictures stored top row first.
	vec2 tone = 0.5 + 0.5 * sin(noise_animation_offset + 6.2831 * nearest_hash);
	tone.y = 1.0 - tone.y;
	vec2 gloss_at = vec2(nearest_hash.x, 1.0 - nearest_hash.y);
	vec3 cell_color = cell_color_tint.rgb;
	if (use_color_gradient_texture) {
		cell_color *= texture(color_gradient_texture, tone).rgb;
	}
	vec3 cell_emission = emission_color.rgb;
	if (use_emission_gradient_texture) {
		cell_emission *= texture(emission_gradient_texture, tone).rgb;
	}
	float smoothness = cell_smoothness;
	if (use_smoothness_gradient_texture) {
		smoothness *= texture(smoothness_gradient_texture, gloss_at).r;
	}

	vec3 pbs_albedo = mix(edge_color.rgb, cell_color, in_cell);
	float pbs_alpha = 1.0;
	vec3 pbs_emission = mix(edge_emission.rgb, cell_emission, in_cell);
	float pbs_occlusion = 1.0;
	bool pbs_use_occlusion = false;
	vec3 pbs_normal = mix(seam_normal, cell_normal, in_cell);
	bool pbs_use_normal = true;
	float pbs_metallic = mix(edge_metallic, cell_metallic, in_cell);
	float pbs_smoothness = mix(edge_smoothness, smoothness, in_cell);

#include "res://Shaders/PBS_Surface_Write.gdshaderinc"
}
