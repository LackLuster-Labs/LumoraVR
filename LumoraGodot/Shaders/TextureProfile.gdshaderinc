// The colour profile a picture was stored in, passed by the material hook beside each texture as
// <slot>_profile: 0 leaves the reading to the slot, 1 is linear, 2 is sRGB. Alpha is never encoded.
#ifndef TEXTURE_PROFILE_INC
#define TEXTURE_PROFILE_INC

vec3 profile_decode(vec3 c) {
	c = max(c, vec3(0.0));
	return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec3 profile_encode(vec3 c) {
	c = max(c, vec3(0.0));
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

// A data slot samples the texel as stored; an sRGB picture bound to it is decoded first.
vec4 profile_data(vec4 texel, int profile) {
	return profile == 2 ? vec4(profile_decode(texel.rgb), texel.a) : texel;
}

// A colour slot's sampler decodes sRGB itself; a linear picture bound to it is put back the way it was stored.
vec4 profile_color(vec4 texel, int profile) {
	return profile == 1 ? vec4(profile_encode(texel.rgb), texel.a) : texel;
}

#endif
