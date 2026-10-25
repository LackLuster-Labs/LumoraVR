shader_type spatial;
// Mesh data shown as colour: clamp(data * data_scale + data_offset, 0, 1) for one of the vertex attributes, in
// the mesh's own space. Normalize shows a vector's direction alone. A set the mesh does not have reads as zero
// (white for the vertex colour). Opaque and unlit. -xlinka
render_mode unshaded, blend_mix, cull_back;

// 0 position, 1 colour, 2 colour alpha, 3 normal, 4 tangent, 5 tangent handedness, 6 bitangent, 7 to 10 the
// four UV sets.
uniform int visualize = 3;
uniform bool normalize_data = false;
uniform float data_scale = 1.0;
uniform vec3 data_offset = vec3(0.0);

varying vec3 shown;

void vertex() {
	vec3 data = VERTEX;
	if (visualize == 1) {
		data = COLOR.rgb;
	} else if (visualize == 2) {
		data = vec3(COLOR.a);
	} else if (visualize == 3) {
		data = NORMAL;
	} else if (visualize == 4) {
		data = TANGENT;
	} else if (visualize == 5) {
		// The handedness is not handed over on its own; it is which way the binormal turned out.
		data = vec3(dot(cross(NORMAL, TANGENT), BINORMAL) < 0.0 ? -1.0 : 1.0);
	} else if (visualize == 6) {
		data = BINORMAL;
	} else if (visualize == 7) {
		data = vec3(UV, 0.0);
	} else if (visualize == 8) {
		data = vec3(UV2, 0.0);
	} else if (visualize == 9) {
		data = vec3(CUSTOM1.xy, 0.0);
	} else if (visualize == 10) {
		data = vec3(CUSTOM1.zw, 0.0);
	}
	shown = data;
}

void fragment() {
	vec3 data = shown;
	if (normalize_data && dot(data, data) > 1e-12) {
		data = normalize(data);
	}
	ALBEDO = clamp(data * data_scale + data_offset, 0.0, 1.0);
}
