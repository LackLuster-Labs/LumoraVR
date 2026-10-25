shader_type spatial;
// Inverted-hull outline: the shell is pushed out along the normal and only its BACK faces are kept,
// so it shows as a border wherever the real surface does not cover it. Godot 4 has no multi-pass
// shaders, so this is attached as the toon material's next_pass by the material hook rather than
// living as a second pass in Mat_FlatToon. -xlinka
//
// Opaque, depth-tested and depth-writing on purpose. The shell's inward-facing fragments sit BEHIND
// the model, so the depth the toon pass already laid down rejects them and only the border survives.
// outline_color's alpha is not used: a blended shell would need the depth write turned off, and then
// the interior fragments would paint straight over the model. -xlinka
render_mode unshaded, cull_front;

uniform vec4 outline_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
// Metres at one metre from the camera. Closer than that the extrusion scales with view depth, so the
// border holds its screen width and never swallows a model you stand next to. Past a metre it stops
// scaling and stays that many metres on the object, thinning with it: scaled all the way out, a small
// thing far away sat inside a fat black disc the size it was at arm's length. -xlinka
uniform float outline_width : hint_range(0.0, 0.1) = 0.0;

void vertex() {
	vec3 view_normal = normalize(MODELVIEW_NORMAL_MATRIX * NORMAL);
	vec3 view_pos = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	view_pos += view_normal * outline_width * clamp(-view_pos.z, 0.01, 1.0);
	POSITION = PROJECTION_MATRIX * vec4(view_pos, 1.0);
	// No width, no hull: every corner on one point outside the view rasterises nothing, where a discard
	// still shaded each back-face pixel in every pass. -xlinka
	if (outline_width <= 0.0) {
		POSITION = vec4(2.0, 2.0, 2.0, 1.0);
	}
}

void fragment() {
	ALBEDO = outline_color.rgb;
}
