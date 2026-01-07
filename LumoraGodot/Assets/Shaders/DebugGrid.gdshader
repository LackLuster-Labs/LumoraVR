shader_type spatial;
render_mode world_vertex_coords;

varying vec3 pos;

void vertex()
{
	pos = VERTEX;
}

void fragment()
{
	//ALBEDO = pos / 10.0;
	vec3 modulo = mod(floor(pos), 2.0);
	vec3 smallModulo = mod(floor(pos * 10.0), 2.0);
	float value = mod(modulo.x + modulo.y + modulo.z, 2.0);
	float smallValue = mod(smallModulo.x + smallModulo.y + smallModulo.z, 2.0);
	ALBEDO = vec3(((value * 0.25) + 0.25) + (smallValue * 0.1));
	//ALBEDO = value > 1.0 ? vec3(0.0) : vec3(0.5);
}
