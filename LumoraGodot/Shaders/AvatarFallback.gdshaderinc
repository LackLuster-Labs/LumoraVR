// Shared body of the cheap stand-in someone else's toon-shaded avatar wears when this viewer simplifies
// avatar shaders. AvatarFallbackMaterial writes the render modes and includes this, and fills the uniforms
// from the toon material: its main texture and colour, its cutout or blend, a two-band shade worked out
// from its own lighting mode, its first glow (map times mask), its first matcap and the UDIM tiles it
// hides. Everything else the toon draws is dropped. A switch it does not use costs a uniform branch.
//
// Defines a variant can set before including:
//   FB_CUTOUT  alpha below the cutoff is cut away
//   FB_BLEND   alpha goes out for the blend the render mode names
//   FB_DOUBLE  the 2x multiply: the colour doubles under blend_mul
//
// Tiling vectors are in mesh UV space (xy scale, zw offset), already turned over from the toon's
// bottom-up v by the material. -xlinka

const vec3 FB_LUMA = vec3(0.299, 0.587, 0.114);

uniform sampler2D main_tex : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 main_color : source_color = vec4(1.0);
uniform vec4 main_st = vec4(1.0, 1.0, 0.0, 0.0);
// 1 reads UV2 for the main texture, the alpha mask, the glow map and the glow mask.
uniform vec4 uv_pick = vec4(0.0);

uniform sampler2D alpha_mask : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 alpha_mask_st = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 alpha_mask_channels = vec4(1.0, 0.0, 0.0, 0.0);
// x: what the mask does to the alpha (0 nothing, 1 replaces it, 2 multiplies, 3 adds, 4 takes away),
// y: added to the alpha after the mask, z: 1 ignores the texture's alpha, w: 1 inverts the mask.
uniform vec4 alpha_rule = vec4(0.0);
uniform float alpha_cutoff = 0.5;
// An additive blend with a source factor of one: the colour adds whole, whatever its alpha.
uniform float alpha_one = 0.0;
uniform float premultiply = 0.0;

// The share of ALBEDO the surface keeps: how the toon shades where a light does not reach, which is
// all the engine's ambient sees. Every light's share is divided back out of it.
uniform vec3 shade_ambient = vec3(1.0);
uniform vec3 shade_ambient_inverse = vec3(1.0);
// A light's share on the side turned away from it, against the lit side.
uniform vec3 shade_dark = vec3(1.0);
// x, y: the half-Lambert band the dark side gives way to the lit one across, z: how much a cast
// shadow counts for the main lights, w: what an omni or spot light keeps on its dark side.
uniform vec4 shade_band = vec4(0.4, 0.6, 0.0, 0.0);
// x: how far the main lights go to their brightness alone, y: the same for omni and spot lights,
// z: the cap on the main lights together, w: the brightness limit of each other light.
uniform vec4 light_rule = vec4(0.0, 0.0, 1.0, 1.0);
uniform float unlit = 0.0;

uniform sampler2D glow_map : source_color, hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D glow_mask : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform vec4 glow_color : source_color = vec4(1.0);
uniform vec4 glow_map_st = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 glow_mask_st = vec4(1.0, 1.0, 0.0, 0.0);
uniform vec4 glow_mask_channels = vec4(1.0, 0.0, 0.0, 0.0);
// x: strength (0 is no glow), y: how far the surface's colour stands in for the map, z: 1 inverts the
// mask, w: 1 has a map to read and 2 a mask, 3 both.
uniform vec4 glow_rule = vec4(0.0);

uniform sampler2D matcap_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform vec4 matcap_color : source_color = vec4(1.0);
uniform vec4 matcap_st = vec4(1.0, 1.0, 0.0, 0.0);
// x: 1 is on, y: the UV mode (0 view normal, 1 top pinch, 2 reflection, 3 edge gradient), z: border,
// w: the mip to read (a rougher matcap reads a blurrier one).
uniform vec4 matcap_uv = vec4(0.0);
// x replace, y multiply, z add, w screen
uniform vec4 matcap_blend = vec4(0.0);
// x mixed, y glow, z how far it takes the surface's colour, w intensity
uniform vec4 matcap_more = vec4(0.0, 0.0, 0.0, 1.0);

// One bit per UDIM tile the material hides (row * 4 + column, rows counted up from the bottom); bit 16
// reads the tile from UV2.
uniform int udim_hide = 0;

void vertex()
{
	if (udim_hide != 0)
	{
		vec2 uv = (udim_hide & 65536) != 0 ? UV2 : UV;
		ivec2 tile = ivec2(floor(vec2(uv.x, 1.0 - uv.y)));
		if (tile.x >= 0 && tile.x < 4 && tile.y >= 0 && tile.y < 4 && ((udim_hide >> (tile.y * 4 + tile.x)) & 1) != 0)
		{
			// NaN drops every triangle the vertex is in.
			VERTEX = vec3(uintBitsToFloat(0x7fc00000u));
		}
	}
}

void fragment()
{
	vec4 tex = texture(main_tex, mix(UV, UV2, uv_pick.x) * main_st.xy + main_st.zw);
	vec3 base = tex.rgb * main_color.rgb;
	float alpha = max(tex.a, alpha_rule.z) * main_color.a;
	if (alpha_rule.x != 0.0)
	{
		float m = clamp(dot(texture(alpha_mask, mix(UV, UV2, uv_pick.y) * alpha_mask_st.xy + alpha_mask_st.zw), alpha_mask_channels), 0.0, 1.0);
		m = mix(m, 1.0 - m, alpha_rule.w);
		alpha = alpha_rule.x < 1.5 ? m : alpha_rule.x < 2.5 ? alpha * m : clamp(alpha_rule.x < 3.5 ? alpha + m : alpha - m, 0.0, 1.0);
	}
	alpha = clamp(alpha + alpha_rule.y, 0.0, 1.0);

	vec3 glow = vec3(0.0);
	if (matcap_uv.x != 0.0)
	{
		vec2 muv;
		if (matcap_uv.y < 0.5)
		{
			vec3 detail = NORMAL * vec3(-1.0, -1.0, 1.0);
			vec3 view = VIEW * vec3(-1.0, -1.0, 1.0) + vec3(0.0, 0.0, 1.0);
			muv = (view * dot(view, detail) / view.z - detail).xy * matcap_uv.z + 0.5;
		}
		else
		{
			vec3 n = mat3(INV_VIEW_MATRIX) * NORMAL;
			vec3 v = mat3(INV_VIEW_MATRIX) * VIEW;
			if (matcap_uv.y < 1.5)
			{
				vec3 up = normalize(vec3(0.0, 1.0, 0.0) - v * v.y);
				vec3 right = normalize(cross(up, v));
				muv = vec2(dot(right, n), dot(up, n)) * matcap_uv.z + 0.5;
			}
			else if (matcap_uv.y < 2.5)
			{
				vec3 r = reflect(-v, n);
				muv = vec2(-r.x, r.y) * matcap_uv.z + 0.5;
			}
			else
			{
				muv = vec2(1.0 - abs(dot(n, v)));
			}
		}
		muv = muv * matcap_st.xy + matcap_st.zw;
		vec4 mc = textureLod(matcap_tex, vec2(muv.x, 1.0 - muv.y), matcap_uv.w) * matcap_color;
		mc.rgb *= matcap_more.w;
		mc.rgb = mix(mc.rgb, mc.rgb * base, matcap_more.z);
		float ma = mc.a;
		base = mix(base, mc.rgb, matcap_blend.x * ma);
		base *= mix(vec3(1.0), mc.rgb, matcap_blend.y * ma);
		base += mc.rgb * (matcap_blend.z * ma);
		base = mix(base, 1.0 - (1.0 - base) * (1.0 - mc.rgb), matcap_blend.w * ma);
		base *= 1.0 + mc.rgb * (matcap_more.x * ma);
		glow += mc.rgb * (matcap_more.y * ma);
	}

	if (glow_rule.x > 0.0)
	{
		vec4 map = vec4(1.0);
		float mask = 1.0;
		if (mod(glow_rule.w, 2.0) > 0.5)
		{
			map = texture(glow_map, mix(UV, UV2, uv_pick.z) * glow_map_st.xy + glow_map_st.zw);
		}
		if (glow_rule.w > 1.5)
		{
			mask = clamp(dot(texture(glow_mask, mix(UV, UV2, uv_pick.w) * glow_mask_st.xy + glow_mask_st.zw), glow_mask_channels), 0.0, 1.0);
		}
		mask = mix(mask, 1.0 - mask, glow_rule.z);
		glow += map.rgb * mix(vec3(1.0), base, glow_rule.y) * glow_color.rgb * (glow_rule.x * map.a * mask);
	}

	base *= mix(1.0, alpha, premultiply);
	ALBEDO = base * shade_ambient * (1.0 - unlit);
	EMISSION = glow + base * unlit;
#if defined(FB_DOUBLE)
	ALBEDO *= 2.0;
	EMISSION *= 2.0;
#endif
	METALLIC = 0.0;
	ROUGHNESS = 1.0;
	SPECULAR = 0.0;
#if defined(FB_CUTOUT)
	ALPHA = alpha;
	ALPHA_SCISSOR_THRESHOLD = alpha_cutoff;
#elif defined(FB_BLEND)
	ALPHA = mix(alpha, 1.0, alpha_one);
#endif
}

void light()
{
	float lit = smoothstep(shade_band.x, shade_band.y, dot(NORMAL, LIGHT) * 0.5 + 0.5);
	vec3 c = LIGHT_COLOR / PI;
	if (LIGHT_IS_DIRECTIONAL)
	{
		lit *= mix(1.0, ATTENUATION, shade_band.z);
		c = mix(c, vec3(dot(c, FB_LUMA)), light_rule.x);
		DIFFUSE_LIGHT = min(DIFFUSE_LIGHT + c * mix(shade_dark, vec3(1.0), lit) * shade_ambient_inverse, light_rule.z * shade_ambient_inverse);
	}
	else
	{
		c *= ATTENUATION;
		c = mix(c, vec3(dot(c, FB_LUMA)), light_rule.y);
		c *= min(1.0, light_rule.w / max(dot(c, FB_LUMA), 1e-4));
		DIFFUSE_LIGHT += c * mix(vec3(shade_band.w), vec3(1.0), lit) * shade_ambient_inverse;
	}
}
