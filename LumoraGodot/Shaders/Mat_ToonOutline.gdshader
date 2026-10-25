shader_type spatial;
// Inverted-hull outline: the shell is pushed out along the normal and only its BACK faces are kept, so
// it shows as a border wherever the real surface does not cover it. Godot 4 has no multi-pass shaders,
// so the material hook attaches this as the toon material's next pass.
//
// Opaque, depth-tested and depth-writing on purpose. The shell's inward-facing fragments sit BEHIND the
// model, so the depth the toon pass already laid down rejects them and only the border survives.
// outline_color's alpha is unused: a blended shell would need the depth write off, and then the
// interior fragments would paint straight over the model. -xlinka
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Metres of extrusion in MODEL space, so the border is a fixed thickness on the object and gets thinner
// with distance the way a drawn line on a model does, rather than the constant screen width the flat
// toon outline holds. On a scaled slot it scales with the slot. -xlinka
// Metres of model-space extrusion, so the useful range is millimetres. 0.1 here is ten centimetres.
uniform float outline_width : hint_range(0.0, 0.005) = 0.0;

void vertex() {
	VERTEX += normalize(NORMAL) * outline_width;
	// No width, no hull: every corner on one point rasterises nothing, where a discard still shaded each
	// back-face pixel in every pass. -xlinka
	if (outline_width <= 0.0) {
		VERTEX = vec3(0.0);
	}
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
