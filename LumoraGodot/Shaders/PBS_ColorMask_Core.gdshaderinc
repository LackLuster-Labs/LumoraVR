// Shared body of the four-way colour-mask metallic material.
// Four-way colour pick from a mask texture's channels, over a metallic PBS surface.
//
//     c      = C0*mask.r + C1*mask.g + C2*mask.b + C3*mask.a
//     weight = saturate(1 / (mask.r + mask.g + mask.b + mask.a))
//     c     *= weight
//
// The weight is the part worth understanding. Without it a pixel with red AND green lit would come
// out as the SUM of two colours and blow past white; with it the sum is divided by how many channels
// are contributing, so that pixel lands halfway between them. The saturate matters too: where the
// mask is entirely black the reciprocal is infinite, saturate pulls it to 1, and the result is the
// zero sum rather than a NaN. Keep the saturate.
//
// METALLIC PACKING IS NOT glTF'S: red is metallic and ALPHA is smoothness. Our other PBS shaders read
// green/blue because that is how a glTF packs it, and reading this one that way puts roughness in an
// empty channel and makes every masked surface uniformly glossy. -xlinka

// ALBEDO
uniform vec4 albedo_color0 : source_color = vec4(1.0, 0.0, 0.0, 1.0);
uniform vec4 albedo_color1 : source_color = vec4(0.0, 1.0, 0.0, 1.0);
uniform vec4 albedo_color2 : source_color = vec4(0.0, 0.0, 1.0, 1.0);
uniform vec4 albedo_color3 : source_color = vec4(1.0);

uniform sampler2D albedo_texture : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_albedo_texture = false;

// THE MASK. Unbound it reads black and yields a black surface - correct, and obvious enough that a
// missing mask looks like a missing mask.
uniform sampler2D color_mask : hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_color_mask = false;

uniform vec2 texture_scale = vec2(1.0);
uniform vec2 texture_offset = vec2(0.0);
uniform vec2 color_mask_scale = vec2(1.0);
uniform vec2 color_mask_offset = vec2(0.0);

// AN OPAQUE MATERIAL MUST IGNORE ITS OWN ALPHA.
//
// Set by the material hook from the component's Transparent flag: 0 Opaque, 1 Cutout, 2 Alpha. The
// uniform was already being written and this shader simply did not declare it, so ALPHA came straight
// off the mask-weighted colour sum no matter what the material asked for. Any mask pixel that is not
// fully saturated, or any colour slot authored with alpha below 1, then made the surface see-through -
// and on a gas mask that is the whole opaque shell of the thing going translucent.
//
// A One Zero blend would discard the alpha output entirely, but Godot's blend_mix always honours
// ALPHA, so the guard has to be explicit. -xlinka
uniform int blend_mode = 0;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

#include "res://Shaders/TextureProfile.gdshaderinc"

// SURFACE
#ifdef PBS_SPECULAR
uniform vec4 specular_color : source_color = vec4(0.2, 0.2, 0.2, 0.25);
uniform sampler2D specular_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_specular_map = false;
uniform int specular_map_profile = 0;
#include "res://Shaders/PBS_Specular_Light.gdshaderinc"
#else
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float smoothness : hint_range(0.0, 1.0) = 0.25;
uniform sampler2D metallic_map : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_metallic_map = false;
uniform int metallic_map_profile = 0;
#endif

uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_normal_map = false;
uniform float normal_scale : hint_range(0.0, 4.0) = 1.0;

uniform sampler2D occlusion_map : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_occlusion_map = false;
uniform int occlusion_map_profile = 0;

// EMISSION, through the same mask.
uniform vec4 emissive_color0 : source_color = vec4(0.0);
uniform vec4 emissive_color1 : source_color = vec4(0.0);
uniform vec4 emissive_color2 : source_color = vec4(0.0);
uniform vec4 emissive_color3 : source_color = vec4(0.0);
uniform sampler2D emissive_map : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool use_emissive_map = false;

void fragment() {
	vec2 albedo_uv = UV * texture_scale + texture_offset;
	vec2 mask_uv = UV * color_mask_scale + color_mask_offset;

	// No mask bound means no pick to make: slot 0 is the colour, which keeps a freshly made material
	// visible instead of black while somebody is still wiring it up.
	vec4 mask = use_color_mask ? texture(color_mask, mask_uv) : vec4(1.0, 0.0, 0.0, 0.0);

	float total = mask.r + mask.g + mask.b + mask.a;
	float weight = clamp(1.0 / total, 0.0, 1.0);

	vec4 c = albedo_color0 * mask.r
	       + albedo_color1 * mask.g
	       + albedo_color2 * mask.b
	       + albedo_color3 * mask.a;
	c *= weight;

	if (use_albedo_texture) {
		c *= texture(albedo_texture, albedo_uv);
	}

	if (blend_mode == 1 && c.a < alpha_cutoff) {
		discard;
	}

	ALBEDO = c.rgb;
	// WRITING ALPHA AT ALL IS WHAT MAKES A MATERIAL TRANSPARENT IN GODOT, whatever value is written.
	// The opaque variant leaves TRANSPARENT_PASS undefined and this write disappears at preprocess, so
	// the shell lands in the opaque pass, writes depth, and stops being sorted against the additive and
	// multiply layers stacked on the same mesh. -xlinka
#ifdef PBS_SPECULAR
	// Reflectance as a colour, smoothness in its alpha; the map replaces the colour. What the specular lobe
	// reflects is not left for the diffuse, by the strongest channel.
	vec4 spec = use_specular_map ? profile_color(texture(specular_map, albedo_uv), specular_map_profile) : specular_color;
	float reflectivity = max(max(spec.r, spec.g), spec.b);
	spec_f0 = spec.rgb;
	ALBEDO = c.rgb * (1.0 - reflectivity);
	METALLIC = 0.0;
	SPECULAR = clamp(sqrt(reflectivity / 0.16), 0.0, 1.0);
	ROUGHNESS = clamp(1.0 - spec.a, 0.0, 1.0);
#else
	float rough = 1.0 - smoothness;
	float metal = metallic;
	if (use_metallic_map) {
		vec4 m = profile_data(texture(metallic_map, albedo_uv), metallic_map_profile);
		metal = m.r;
		rough = 1.0 - m.a;
	}
	METALLIC = clamp(metal, 0.0, 1.0);
	ROUGHNESS = clamp(rough, 0.0, 1.0);
	float reflectivity = mix(0.04, 1.0, clamp(metal, 0.0, 1.0));
#endif

	// Blended is premultiplied: the diffuse fades with alpha and the reflections keep their strength.
#ifdef TRANSPARENT_PASS
	ALBEDO *= c.a;
	ALPHA = reflectivity + c.a * (1.0 - reflectivity);
#endif

	// Occlusion darkens only the light from the surroundings; direct lights are left alone.
	if (use_occlusion_map) {
		AO = profile_data(texture(occlusion_map, albedo_uv), occlusion_map_profile).r;
		AO_LIGHT_AFFECT = 0.0;
	}

	vec4 emission = emissive_color0 * mask.r
	              + emissive_color1 * mask.g
	              + emissive_color2 * mask.b
	              + emissive_color3 * mask.a;
	emission *= weight;
	if (use_emissive_map) {
		emission *= texture(emissive_map, albedo_uv);
	}
	// The emission colours' alpha is not a strength.
	EMISSION = emission.rgb;

	if (use_normal_map) {
		vec3 tn = texture(normal_map, albedo_uv).xyz * 2.0 - 1.0;
		tn.xy *= normal_scale;
		NORMAL = normalize(TANGENT * tn.x + BINORMAL * tn.y + NORMAL * tn.z);
	}
}
