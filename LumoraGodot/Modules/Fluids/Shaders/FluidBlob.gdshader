// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

// Settled fluid on machines that do not simulate (Android, view only): each ledger cell as a flattened blob in one
// MultiMesh, in the goo look and opaque, cheap enough for a headset's Mobile renderer. The instance colour is the
// cell's tint; INSTANCE_CUSTOM.x its wetness. A soft rim keeps neighbouring blobs from reading as beads. A liquid's
// cells (liquid 1) are dark and glassy instead. -xlinka

shader_type spatial;
render_mode cull_back, depth_draw_opaque, diffuse_burley, specular_schlick_ggx;

#include "res://Modules/Fluids/Shaders/FluidLook.gdshaderinc"

varying float blob_wetness;

void vertex()
{
	blob_wetness = INSTANCE_CUSTOM.x;
}

void fragment()
{
	float facing = clamp(dot(NORMAL, VIEW), 0.0, 1.0);
	ALBEDO = tint.rgb * COLOR.rgb * mix(0.75, 1.0, facing);
	ROUGHNESS = fluid_roughness(blob_wetness);
	METALLIC = 0.0;
	SPECULAR = 0.5;
	BACKLIGHT = fluid_backlight(COLOR.rgb, 0.004);
	EMISSION = emission.rgb;
	if (liquid > 0.5)
	{
		// water settled on a machine that does not simulate: dark and glassy, so the sky and the lights do the work
		ALBEDO = tint.rgb * COLOR.rgb * mix(0.18, 0.3, facing);
		ROUGHNESS = 0.05;
		BACKLIGHT = vec3(0.0);
	}
}
