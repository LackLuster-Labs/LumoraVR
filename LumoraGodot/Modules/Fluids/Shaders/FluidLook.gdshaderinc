// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// The goo look the bulk, the strands, the spots and the view-only blobs share: FluidMaterial's uniforms (set by
// name) and the few functions that turn a surface's tint, thickness, speed and wetness into a lit surface. Goo
// uses transmission for the scene behind as well as light through thin sheets.
// -xlinka

// multiplies the fluid's own colour (vertex or instance COLOR, the substance's tint)
uniform vec4 tint : source_color = vec4(1.0, 1.0, 1.0, 1.0);
// extinction per cm, per channel
uniform vec4 absorption = vec4(0.0);
uniform float colour_depth = 6.0;
uniform float gloss : hint_range(0.0, 1.0) = 0.85;
uniform float clearcoat : hint_range(0.0, 1.0) = 0.5;
uniform float transmission : hint_range(0.0, 1.0) = 0.7;
uniform float foam : hint_range(0.0, 1.0) = 0.0;
uniform vec4 emission : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// 1 for a liquid's spray and view-only blobs: clear and glassy instead of goo (the renderer sets it from the look)
uniform float liquid : hint_range(0.0, 1.0) = 0.0;

// Beer-Lambert through a thickness in metres.
vec3 fluid_transmittance(vec3 vertex_colour, float thickness)
{
	vec3 colour = clamp(tint.rgb * vertex_colour, vec3(0.02), vec3(1.0));
	vec3 extinction = dot(absorption.rgb, vec3(1.0)) > 0.0 ? max(absorption.rgb, vec3(0.0)) : -log(colour) / max(colour_depth, 0.01);
	// Raster interpolation can extrapolate thin triangles; a negative path would amplify light. -xlinka
	return exp(-extinction * (max(thickness, 0.0) * 100.0));
}

// Body colour: the tint, the particles' own colour, deeper where the goo is thick, whiter where it moves fast
// (foam, for the liquid presets).
vec3 fluid_albedo(vec3 vertex_colour, float thickness, float speed)
{
	vec3 body = tint.rgb * vertex_colour;
	body *= mix(vec3(1.0), sqrt(fluid_transmittance(vertex_colour, thickness)), 0.6);
	return mix(body, vec3(1.0), foam * smoothstep(1.5, 4.0, speed));
}

// Wet goo is glossy; as it dries it goes matte.
float fluid_roughness(float wetness)
{
	return mix(0.65, clamp(1.0 - gloss, 0.03, 1.0), clamp(wetness, 0.0, 1.0));
}

// Filter highlights smaller than a pixel using the surface normal's local variation. -xlinka
float fluid_filtered_roughness(vec3 view_normal, float roughness)
{
	vec3 n = normalize(view_normal);
	vec3 dx = dFdx(n);
	vec3 dy = dFdy(n);
	float variance = 0.5 * (dot(dx, dx) + dot(dy, dy));
	return sqrt(clamp(roughness * roughness + variance, 0.0, 1.0));
}

// Light from behind through a thin sheet or a thread.
vec3 fluid_backlight(vec3 vertex_colour, float thickness)
{
	return tint.rgb * vertex_colour * transmission * fluid_transmittance(vertex_colour, thickness);
}

// Bulk and strands use the same absorbed scene and diffuse response. -xlinka
void fluid_goo_surface(vec3 vertex_colour, float thickness, float speed, float wetness, float facing, vec3 behind,
	out vec3 albedo, out vec3 emitted, out float cover)
{
	vec3 through = fluid_transmittance(vertex_colour, thickness);
	float transmitted = clamp(transmission * wetness, 0.0, 1.0);
	// The scene behind is already transmitted below; coverage must not blend it in again. -xlinka
	cover = 1.0;
	float fresnel = 0.04 + 0.96 * pow(1.0 - facing, 5.0);
	albedo = fluid_albedo(vertex_colour, thickness, speed) * (1.0 - transmitted) * cover;
	emitted = behind * through * transmitted * (1.0 - fresnel) * cover + emission.rgb;
}
