// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Poiyomi's shared helpers: color, blend, mask, random, rotation, edge and camera math, and the global
// theme colors.
// Helpers that read the camera or the object take PoiMesh / PoiCam first. Spherical harmonics, lightmaps,
// shadow masks, baked GI and the camera-mode globals have no counterpart here: the environment supplies
// ambient and reflections.

#define EPSILON 0.000001
#define PI_OVER_2 1.5707963
#define PI_OVER_4 0.785398
#define HALF_PI 1.5707964
#define TWO_PI 6.28318530718
#define INV_PI 0.31830988618
#define INV_TWO_PI 0.15915494309
#define DielectricSpec vec4(0.04, 0.04, 0.04, 1.0 - 0.04)

// Unity's linear color space constants.
#define POI_COLOR_SPACE_GREY vec4(0.214041144, 0.214041144, 0.214041144, 0.5)
#define POI_COLOR_SPACE_DOUBLE vec4(4.59479380, 4.59479380, 4.59479380, 2.0)
#define POI_COLOR_SPACE_DIELECTRIC_SPEC vec4(0.04, 0.04, 0.04, 1.0 - 0.04)
#define POI_COLOR_SPACE_LUMINANCE vec4(0.0396819152, 0.458021790, 0.00609653955, 1.0)
#define POI_SPECCUBE_LOD_STEPS 6.0

#define POI_SAFE_RGB0 vec4(mainTexture.rgb * 0.0001, 0.0)
#define POI_SAFE_RGB1 vec4(mainTexture.rgb * 0.0001, 1.0)
#define POI_SAFE_RGBA mainTexture

#define POI_ERROR(poiMesh, gridSize) mix(vec3(1.0, 0.0, 1.0), vec3(0.0), float(mod(floor((poiMesh.worldPos.x) * gridSize) + floor((poiMesh.worldPos.y) * gridSize) + floor((poiMesh.worldPos.z) * gridSize), 2.0) == 0.0))
#define POI_NAN uintBitsToFloat(4294967295u)

// glsl_mod behaves better on negative numbers, and
// in some situations actually outperforms HLSL's fmod()
#define glsl_mod(x, y) ((x) - (y) * floor((x) / (y)))

// sin/cos handling for large values
#define mod2pi(x) (fract((x) / TAU) * TAU)

// HLSL's fmod: the result takes the sign of x.
float poiFmod(float x, float y)
{
	return x - y * trunc(x / y);
}

vec2 poiFmod(vec2 x, vec2 y)
{
	return x - y * trunc(x / y);
}

vec3 poiFmod(vec3 x, vec3 y)
{
	return x - y * trunc(x / y);
}

vec4 poiFmod(vec4 x, vec4 y)
{
	return x - y * trunc(x / y);
}

// Clip position of the fragment (pixel xy from the top-left, depth, w) to a screen position whose xy / w
// is Poiyomi's screen UV, y up.
vec4 poiTransformClipSpacetoScreenSpaceFrag(in PoiCam poiCam, vec4 clipPos)
{
	vec4 positionSS = vec4(clipPos.xyz * clipPos.w, clipPos.w);
	positionSS.y = (poiCam.screenSize.y - clipPos.y) * clipPos.w;
	positionSS.xy = positionSS.xy / poiCam.screenSize;
	return positionSS;
}

float Pow5(float x)
{
	return x * x * x * x * x;
}

vec3 FresnelLerp(vec3 F0, vec3 F90, float cosA)
{
	float t = Pow5((1.0 - cosA));
	return mix(F0, F90, t);
}

vec3 FresnelTerm(vec3 F0, float cosA)
{
	float t = Pow5((1.0 - cosA));
	return F0 + (1.0 - F0) * t;
}

float perceptualRoughnessToMipmapLevel(float perceptualRoughness)
{
	return perceptualRoughness * POI_SPECCUBE_LOD_STEPS;
}

// Normal maps arrive as two- or three-channel textures; every unpack variant rebuilds z from xy.
vec3 UnpackScaleNormalDXT5nm(vec4 packednormal, float bumpScale)
{
	return poiUnpackNormal(packednormal, bumpScale);
}

vec3 LerpWhiteTo(vec3 b, float t)
{
	float oneMinusT = 1.0 - t;
	return vec3(oneMinusT, oneMinusT, oneMinusT) + b * t;
}

float GGXTerm(float NdotH, float roughness)
{
	float a2 = roughness * roughness;
	float d = (NdotH * a2 - NdotH) * NdotH + 1.0;
	return INV_PI * a2 / (d * d + 1e-7);
}

vec3 UnpackScaleNormalRGorAG(vec4 packednormal, float bumpScale)
{
	return poiUnpackNormal(packednormal, bumpScale);
}

vec3 UnpackScaleNormal(vec4 packednormal, float bumpScale)
{
	return UnpackScaleNormalRGorAG(packednormal, bumpScale);
}

vec3 BlendNormals(vec3 n1, vec3 n2)
{
	return normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
}

vec2 Pow4(vec2 x)
{
	return x * x * x * x;
}

vec3 Unity_SafeNormalize(vec3 inVec)
{
	float dp3 = max(0.001, dot(inVec, inVec));
	return inVec * inversesqrt(dp3);
}

vec3 BoxProjectedCubemapDirection(vec3 worldRefl, vec3 worldPos, vec4 cubemapCenter, vec4 boxMin, vec4 boxMax)
{
	if (cubemapCenter.w > 0.0)
	{
		vec3 nrdir = normalize(worldRefl);

		vec3 rbmax = (boxMax.xyz - worldPos) / nrdir;
		vec3 rbmin = (boxMin.xyz - worldPos) / nrdir;

		vec3 rbminmax = vec3(nrdir.x > 0.0 ? rbmax.x : rbmin.x, nrdir.y > 0.0 ? rbmax.y : rbmin.y, nrdir.z > 0.0 ? rbmax.z : rbmin.z);

		float fa = min(min(rbminmax.x, rbminmax.y), rbminmax.z);

		worldPos -= cubemapCenter.xyz;
		worldRefl = worldPos + nrdir * fa;
	}
	return worldRefl;
}

// Convert rgb to luminance
// with rgb in linear space with sRGB primaries and D65 white point
float LinearRgbToLuminance(vec3 linearRgb)
{
	return dot(linearRgb, vec3(0.2126729, 0.7151522, 0.0721750));
}

// Eye depth over the far plane for a raw (reversed) depth sample.
float Linear01Depth(in PoiCam poiCam, float rawDepth)
{
	return poiLinearEyeDepth(poiCam, rawDepth) / poiCam.farPlane;
}

vec3 LinearToGammaSpace(vec3 linRGB)
{
	linRGB = max(linRGB, vec3(0.0));
	// An almost-perfect approximation from http://chilliant.blogspot.com.au/2012/08/srgb-approximations-for-hlsl.html?m=1
	return max(1.055 * pow(linRGB, vec3(0.416666667)) - 0.055, vec3(0.0));
}

vec3 GammaToLinearSpace(vec3 sRGB)
{
	// Approximate version from http://chilliant.blogspot.com.au/2012/08/srgb-approximations-for-hlsl.html?m=1
	return sRGB * (sRGB * (sRGB * 0.305306011 + 0.682171111) + 0.012522878);
}

vec3 PoiUnpackNormalScale(vec4 packedNormal, float bumpScale)
{
	return poiUnpackNormal(packedNormal, bumpScale);
}

vec3 PoiObjectToWorldNormal(in PoiMesh poiMesh, vec3 normal)
{
	return normalize(normal * mat3(poiMesh.invModelMatrix));
}

vec3 PoiObjectToWorldNormal(in PoiMesh poiMesh, vec3 normal, bool doNormalize)
{
	return PoiObjectToWorldNormal(poiMesh, normal);
}

vec4 PoiObjectToClipPos(in PoiMesh poiMesh, in PoiCam poiCam, vec3 pos)
{
	return poiCam.projectionMatrix * (poiCam.viewMatrix * (poiMesh.modelMatrix * vec4(pos, 1.0)));
}

vec4 PoiWorldToClipPos(in PoiCam poiCam, vec3 pos)
{
	return poiCam.projectionMatrix * (poiCam.viewMatrix * vec4(pos, 1.0));
}

// xy / w is Poiyomi's screen UV (y up) for a clip position from PoiObjectToClipPos / PoiWorldToClipPos.
vec4 PoiComputeScreenPos(in PoiCam poiCam, vec4 pos)
{
	vec4 o = pos * 0.5;
	o.xy = vec2(o.x, -o.y) + o.w;
	o.zw = pos.zw;
	return o;
}

// Cubemaps are linear: no HDR decode.
vec3 PoiDecodeHDR(vec4 data, vec4 decodeInstructions)
{
	return data.rgb;
}

vec3 PoiDecodeHDR(vec4 data)
{
	return data.rgb;
}

vec3 PoiWorldSpaceViewDir(in PoiCam poiCam, vec3 worldPos)
{
	return poiCam.invViewMatrix[3].xyz - worldPos;
}

vec3 PoiObjSpaceViewDir(in PoiMesh poiMesh, in PoiCam poiCam, vec3 localPos)
{
	return (poiMesh.invModelMatrix * vec4(poiCam.invViewMatrix[3].xyz, 1.0)).xyz - localPos;
}

vec3 PoiObjSpaceViewDir(in PoiMesh poiMesh, in PoiCam poiCam, vec4 v)
{
	return PoiObjSpaceViewDir(poiMesh, poiCam, v.xyz);
}

vec2 poiUV(vec2 uv, vec4 tex_st)
{
	return uv * tex_st.xy + tex_st.zw;
}

//Lighting Helpers
float CalculateLuminance(vec3 color)
{
	return color.r * 0.299 + color.g * 0.587 + color.b * 0.114;
}

float AverageLuminance(vec3 color)
{
	return (color.r + color.g + color.b) / 3.0;
}

vec3 PoiMinBrightness(vec3 color, float minBrightness)
{
	float linearMinBrightness = minBrightness;
	float noiseFloor = max(linearMinBrightness * 0.01, 0.005);
	float lum = CalculateLuminance(color);
	vec3 scaled = color * (max(lum, linearMinBrightness) / max(lum, 1e-4));
	return mix(max(color, vec3(linearMinBrightness)), scaled, smoothstep(noiseFloor * 0.5, noiseFloor, lum));
}

float dotToDegrees(float d)
{
	d = clamp(d, -1.0, 1.0);
	return degrees(acos(d));
}

// Convenience overload: compute dot for you
float dotToDegrees(vec3 a, vec3 b)
{
	return dotToDegrees(dot(normalize(a), normalize(b)));
}

// Fast approximation of arctangent function
// From Heitz et al. 2016, "Real-Time Polygonal-Light Shading with Linearly Transformed Cosines"
float poiFastAtan(float x)
{
	return x * (abs(x) * (1.5707963 * abs(x) - 0.00507668) + 0.420691) /
	(abs(x) * (abs(x) * (0.633387806 + abs(x)) + 0.671041944) + 0.215192627);
}

bool IsInMirror(in PoiCam poiCam)
{
	return poiCam.isMirror;
}

// Works with generic oblique-projection mirrors.
bool PoiIsMirrorCamera(in PoiCam poiCam)
{
	return IsInMirror(poiCam);
}

bool IsOrthographicCamera(in PoiCam poiCam)
{
	return poiCam.isOrthographic;
}

// Unity's camera-to-world has +Z forward; origin minus its +Z point is the view's +Z axis (backward).
vec3 getCameraForward(in PoiCam poiCam)
{
	vec3 forward = vec3(0.0, 0.0, 1.0);
	forward = normalize(poiCam.invViewMatrix[2].xyz);
	return forward;
}

vec3 BoxProjection(vec3 direction, vec3 position, vec4 cubemapPosition, vec3 boxMin, vec3 boxMax)
{
	if (cubemapPosition.w > 0.0)
	{
		vec3 factors = (vec3(direction.x > 0.0 ? boxMax.x : boxMin.x, direction.y > 0.0 ? boxMax.y : boxMin.y, direction.z > 0.0 ? boxMax.z : boxMin.z) - position) / direction;
		float scalar = min(min(factors.x, factors.y), factors.z);
		direction = direction * scalar + (position - cubemapPosition.xyz);
	}
	return direction;
}

float poiMax(vec2 i)
{
	return max(i.x, i.y);
}

float poiMax(vec3 i)
{
	return max(max(i.x, i.y), i.z);
}

float poiMax(vec4 i)
{
	return max(max(max(i.x, i.y), i.z), i.w);
}

// calculateNormal fetches at the call site: a function may only take textures that share one filter and
// repeat setting.
vec3 poiCalculateNormalFromTexel(in vec3 baseNormal, in PoiMesh poiMesh, in vec4 packedNormal, in float normalIntensity)
{
	vec3 normal = PoiUnpackNormalScale(packedNormal, normalIntensity);
	return normalize(
	normal.x * poiMesh.tangent[0] +
	normal.y * poiMesh.binormal[0] +
	normal.z * baseNormal
	);
}

#define calculateNormal(baseNormal, poiMesh, normalTexture, normal_ST, normalPan, normalUV, normalIntensity) poiCalculateNormalFromTexel(baseNormal, poiMesh, poiTex(normalTexture, POI_PAN_UV(poiUV((poiMesh).uv[int(normalUV)], normal_ST), normalPan)), normalIntensity)

float remap(float x, float minOld, float maxOld, float minNew, float maxNew)
{
	return minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld);
}

float remap(float x, float minOld, float maxOld)
{
	return remap(x, minOld, maxOld, 0.0, 1.0);
}

vec2 remap(vec2 x, vec2 minOld, vec2 maxOld, vec2 minNew, vec2 maxNew)
{
	return minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld);
}

vec2 remap(vec2 x, vec2 minOld, vec2 maxOld)
{
	return remap(x, minOld, maxOld, vec2(0.0), vec2(1.0));
}

vec3 remap(vec3 x, vec3 minOld, vec3 maxOld, vec3 minNew, vec3 maxNew)
{
	return minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld);
}

vec3 remap(vec3 x, vec3 minOld, vec3 maxOld)
{
	return remap(x, minOld, maxOld, vec3(0.0), vec3(1.0));
}

vec4 remap(vec4 x, vec4 minOld, vec4 maxOld, vec4 minNew, vec4 maxNew)
{
	return minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld);
}

vec4 remap(vec4 x, vec4 minOld, vec4 maxOld)
{
	return remap(x, minOld, maxOld, vec4(0.0), vec4(1.0));
}

float remapClamped(float minOld, float maxOld, float x, float minNew, float maxNew)
{
	return clamp(minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld), minNew, maxNew);
}

float remapClamped(float minOld, float maxOld, float x)
{
	return remapClamped(minOld, maxOld, x, 0.0, 1.0);
}

vec2 remapClamped(vec2 minOld, vec2 maxOld, vec2 x, vec2 minNew, vec2 maxNew)
{
	return clamp(minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld), minNew, maxNew);
}

vec2 remapClamped(vec2 minOld, vec2 maxOld, vec2 x)
{
	return remapClamped(minOld, maxOld, x, vec2(0.0), vec2(1.0));
}

vec3 remapClamped(vec3 minOld, vec3 maxOld, vec3 x, vec3 minNew, vec3 maxNew)
{
	return clamp(minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld), minNew, maxNew);
}

vec3 remapClamped(vec3 minOld, vec3 maxOld, vec3 x)
{
	return remapClamped(minOld, maxOld, x, vec3(0.0), vec3(1.0));
}

vec4 remapClamped(vec4 minOld, vec4 maxOld, vec4 x, vec4 minNew, vec4 maxNew)
{
	return clamp(minNew + (x - minOld) * (maxNew - minNew) / (maxOld - minOld), minNew, maxNew);
}

vec4 remapClamped(vec4 minOld, vec4 maxOld, vec4 x)
{
	return remapClamped(minOld, maxOld, x, vec4(0.0), vec4(1.0));
}

vec2 calcParallax(in float height, in PoiCam poiCam)
{
	return ((height * -1.0) + 1.0) * (poiCam.tangentViewDir.xy / poiCam.tangentViewDir.z);
}

/*
0: Zero	                float4(0.0, 0.0, 0.0, 0.0),
1: One	                float4(1.0, 1.0, 1.0, 1.0),
2: DstColor	            destinationColor,
3: SrcColor	            sourceColor,
4: OneMinusDstColor	    float4(1.0, 1.0, 1.0, 1.0) - destinationColor,
5: SrcAlpha	            sourceColor.aaaa,
6: OneMinusSrcColor	    float4(1.0, 1.0, 1.0, 1.0) - sourceColor,
7: DstAlpha	            destinationColor.aaaa,
8: OneMinusDstAlpha	    float4(1.0, 1.0, 1.0, 1.0) - destinationColor.,
9: SrcAlphaSaturate     saturate(sourceColor.aaaa),
10: OneMinusSrcAlpha	float4(1.0, 1.0, 1.0, 1.0) - sourceColor.aaaa,
*/

vec4 poiBlend(float sourceFactor, vec4 sourceColor, float destinationFactor, vec4 destinationColor, vec4 blendFactor)
{
	vec4 sA = 1.0 - blendFactor;
	vec4 blendData[11] = vec4[11](
		vec4(0.0, 0.0, 0.0, 0.0),
		vec4(1.0, 1.0, 1.0, 1.0),
		destinationColor,
		sourceColor,
		vec4(1.0, 1.0, 1.0, 1.0) - destinationColor,
		sA,
		vec4(1.0, 1.0, 1.0, 1.0) - sourceColor,
		sA,
		vec4(1.0, 1.0, 1.0, 1.0) - sA,
		clamp(sourceColor.aaaa, 0.0, 1.0),
		1.0 - sA
	);

	return mix(blendData[int(sourceFactor)] * sourceColor + blendData[int(destinationFactor)] * destinationColor, sourceColor, sA);
}

// Color burn
float blendColorBurn(float base, float blend)
{
	return (blend == 0.0) ? blend : max((1.0 - ((1.0 - base) * (1.0 / blend))), 0.0);
}

vec3 blendColorBurn(vec3 base, vec3 blend)
{
	return vec3(blendColorBurn(base.r, blend.r), blendColorBurn(base.g, blend.g), blendColorBurn(base.b, blend.b));
}

// Color Dodge
float blendColorDodge(float base, float blend)
{
	return (blend == 1.0) ? blend : min(base / (1.0 - blend), 1.0);
}

vec3 blendColorDodge(vec3 base, vec3 blend)
{
	return vec3(blendColorDodge(base.r, blend.r), blendColorDodge(base.g, blend.g), blendColorDodge(base.b, blend.b));
}

// Darken
float blendDarken(float base, float blend)
{
	return min(blend, base);
}

vec3 blendDarken(vec3 base, vec3 blend)
{
	return vec3(blendDarken(base.r, blend.r), blendDarken(base.g, blend.g), blendDarken(base.b, blend.b));
}

// Overlay
float blendOverlay(float base, float blend)
{
	return base < 0.5 ? (2.0 * base * blend) : (1.0 - 2.0 * (1.0 - base) * (1.0 - blend));
}

vec3 blendOverlay(vec3 base, vec3 blend)
{
	return vec3(blendOverlay(base.r, blend.r), blendOverlay(base.g, blend.g), blendOverlay(base.b, blend.b));
}

// Lighten
float blendLighten(float base, float blend)
{
	return max(blend, base);
}

vec3 blendLighten(vec3 base, vec3 blend)
{
	return vec3(blendLighten(base.r, blend.r), blendLighten(base.g, blend.g), blendLighten(base.b, blend.b));
}

// Linear Dodge
float blendLinearDodge(float base, float blend)
{
	// Note : Same implementation as BlendAddf
	return min(base + blend, 1.0);
}

vec3 blendLinearDodge(vec3 base, vec3 blend)
{
	// Note : Same implementation as BlendAdd
	return base + blend;
}

// Multiply
float blendMultiply(float base, float blend)
{
	return base * blend;
}

vec3 blendMultiply(vec3 base, vec3 blend)
{
	return base * blend;
}

// Normal
float blendNormal(float base, float blend)
{
	return blend;
}

vec3 blendNormal(vec3 base, vec3 blend)
{
	return blend;
}

// Screen
float blendScreen(float base, float blend)
{
	return 1.0 - ((1.0 - base) * (1.0 - blend));
}

vec3 blendScreen(vec3 base, vec3 blend)
{
	return vec3(blendScreen(base.r, blend.r), blendScreen(base.g, blend.g), blendScreen(base.b, blend.b));
}

// Subtract
float blendSubtract(float base, float blend)
{
	return max(base - blend, 0.0);
}

vec3 blendSubtract(vec3 base, vec3 blend)
{
	return max(base - blend, vec3(0.0));
}

float blendMixed(float base, float blend)
{
	return base + base * blend;
}

vec3 blendMixed(vec3 base, vec3 blend)
{
	return base + base * blend;
}

vec3 customBlend(vec3 base, vec3 blend, float blendType, float alpha)
{
	// Compute all blend results
	vec3 r0 = mix(base, blend, alpha);                         // Normal (0)
	vec3 r1 = mix(base, blendDarken(base, blend), alpha);      // Darken (1)
	vec3 r2 = base * mix(vec3(1.0), blend, alpha);             // Multiply (2)
	vec3 r5 = mix(base, blendLighten(base, blend), alpha);     // Lighten (5)
	vec3 r6 = mix(base, blendScreen(base, blend), alpha);      // Screen (6)
	vec3 r7 = blendSubtract(base, blend * alpha);              // Subtract (7)
	vec3 r8 = mix(base, blendLinearDodge(base, blend), alpha); // Linear Dodge (8)
	vec3 r9 = mix(base, blendOverlay(base, blend), alpha);     // Overlay (9)
	vec3 r20 = mix(base, blendMixed(base, blend), alpha);      // Mixed (20)

	// Branchless selection
	float t = blendType;
	float w0 = clamp(1.0 - abs(t - 0.0), 0.0, 1.0);
	float w1 = clamp(1.0 - abs(t - 1.0), 0.0, 1.0);
	float w2 = clamp(1.0 - abs(t - 2.0), 0.0, 1.0);
	float w5 = clamp(1.0 - abs(t - 5.0), 0.0, 1.0);
	float w6 = clamp(1.0 - abs(t - 6.0), 0.0, 1.0);
	float w7 = clamp(1.0 - abs(t - 7.0), 0.0, 1.0);
	float w8 = clamp(1.0 - abs(t - 8.0), 0.0, 1.0);
	float w9 = clamp(1.0 - abs(t - 9.0), 0.0, 1.0);
	float w20 = clamp(1.0 - abs(t - 20.0), 0.0, 1.0);

	return r0 * w0 + r1 * w1 + r2 * w2 + r5 * w5 + r6 * w6 + r7 * w7 + r8 * w8 + r9 * w9 + r20 * w20;
}

vec3 customBlend(vec3 base, vec3 blend, float blendType)
{
	return customBlend(base, blend, blendType, 1.0);
}

float customBlend(float base, float blend, float blendType, float alpha)
{
	// Compute all blend results
	float r0 = mix(base, blend, alpha);                         // Normal (0)
	float r2 = base * mix(1.0, blend, alpha);                   // Multiply (2)
	float r5 = mix(base, blendLighten(base, blend), alpha);     // Lighten (5)
	float r6 = mix(base, blendScreen(base, blend), alpha);      // Screen (6)
	float r7 = blendSubtract(base, blend * alpha);              // Subtract (7)
	float r8 = mix(base, blendLinearDodge(base, blend), alpha); // Linear Dodge (8)
	float r9 = mix(base, blendOverlay(base, blend), alpha);     // Overlay (9)
	float r20 = mix(base, blendMixed(base, blend), alpha);      // Mixed (20)

	// Branchless selection
	float t = blendType;
	float w0 = clamp(1.0 - abs(t - 0.0), 0.0, 1.0);
	float w2 = clamp(1.0 - abs(t - 2.0), 0.0, 1.0);
	float w5 = clamp(1.0 - abs(t - 5.0), 0.0, 1.0);
	float w6 = clamp(1.0 - abs(t - 6.0), 0.0, 1.0);
	float w7 = clamp(1.0 - abs(t - 7.0), 0.0, 1.0);
	float w8 = clamp(1.0 - abs(t - 8.0), 0.0, 1.0);
	float w9 = clamp(1.0 - abs(t - 9.0), 0.0, 1.0);
	float w20 = clamp(1.0 - abs(t - 20.0), 0.0, 1.0);

	return r0 * w0 + r2 * w2 + r5 * w5 + r6 * w6 + r7 * w7 + r8 * w8 + r9 * w9 + r20 * w20;
}

float customBlend(float base, float blend, float blendType)
{
	return customBlend(base, blend, blendType, 1.0);
}

// 0 replace, 1 subtract, 2 multiply, 3 divide, 4 min, 5 max, 6 average, 7 add.
float maskBlend(float baseMask, float blendMask, float blendType)
{
	// Compute all blend results (GPU parallelism)
	float replace = blendMask;
	float subtract = baseMask - blendMask;
	float multiply = baseMask * blendMask;
	float divide = baseMask / max(blendMask, 0.0001);
	float minVal = min(baseMask, blendMask);
	float maxVal = max(baseMask, blendMask);
	float average = (baseMask + blendMask) * 0.5;
	float add = baseMask + blendMask;

	// Branchless selection using step functions
	float t = blendType + 0.5;
	float w0 = step(t, 1.0);
	float w1 = step(1.0, t) * step(t, 2.0);
	float w2 = step(2.0, t) * step(t, 3.0);
	float w3 = step(3.0, t) * step(t, 4.0);
	float w4 = step(4.0, t) * step(t, 5.0);
	float w5 = step(5.0, t) * step(t, 6.0);
	float w6 = step(6.0, t) * step(t, 7.0);
	float w7 = step(7.0, t);

	float output_ = replace * w0 + subtract * w1 + multiply * w2 + divide * w3 + minVal * w4 + maxVal * w5 + average * w6 + add * w7;
	return clamp(output_, 0.0, 1.0);
}

float globalMaskBlend(float baseMask, float globalMaskIndex, float blendType, PoiMods poiMods)
{
	if (globalMaskIndex == 0.0)
	{
		return baseMask;
	}
	else
	{
		return maskBlend(baseMask, poiMods.globalMask[clamp(int(globalMaskIndex) - 1, 0, 15)], blendType);
	}
}

// Standardized mask processing with channel blending, remapping, gamma, and inversion
float poiSampleMask(vec4 sampledTexture, vec4 channelStrengths, float blendMode, float invert, vec2 remapMinMax, float gamma, float strength, float offset)
{
	// Start with R channel weighted by its strength
	float mask = sampledTexture.r * channelStrengths.x;

	// Sequentially blend G, B, A channels
	mask = mix(mask, maskBlend(mask, sampledTexture.g, blendMode), channelStrengths.y);
	mask = mix(mask, maskBlend(mask, sampledTexture.b, blendMode), channelStrengths.z);
	mask = mix(mask, maskBlend(mask, sampledTexture.a, blendMode), channelStrengths.w);

	// Min/Max remapping (remap [min,max] to [0,1])
	mask = clamp((mask - remapMinMax.x) / max(remapMinMax.y - remapMinMax.x, 0.0001), 0.0, 1.0);

	// Gamma curve
	mask = pow(mask, gamma);

	// Strength and offset
	mask = clamp(mask * strength + offset, 0.0, 1.0);

	// Branchless inversion
	mask = mix(mask, 1.0 - mask, invert);

	return mask;
}

// Single channel selection overload (backward compatible)
float poiSampleMask(vec4 sampledTexture, float channel, float invert)
{
	vec4 channelStrengths = vec4(
	float(channel < 0.5),
	float(channel >= 0.5 && channel < 1.5),
	float(channel >= 1.5 && channel < 2.5),
	float(channel >= 2.5)
	);
	return poiSampleMask(sampledTexture, channelStrengths, 0.0, invert, vec2(0.0, 1.0), 1.0, 1.0, 0.0);
}

// Single channel with strength and offset overload
float poiSampleMask(vec4 sampledTexture, float channel, float invert, float strength, float offset)
{
	vec4 channelStrengths = vec4(
	float(channel < 0.5),
	float(channel >= 0.5 && channel < 1.5),
	float(channel >= 1.5 && channel < 2.5),
	float(channel >= 2.5)
	);
	return poiSampleMask(sampledTexture, channelStrengths, 0.0, invert, vec2(0.0, 1.0), 1.0, strength, offset);
}

// Generic Poiyomi RNG helpers (deterministic across stages)
float poiRand(vec2 co)
{
	vec3 p3 = fract(vec3(co.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

vec4 poiRand4(vec2 seed)
{
	vec3 p3 = fract(vec3(seed.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	vec2 a = fract((p3.xx + p3.yz) * p3.zy);
	vec2 s2 = seed + 37.0;
	vec3 q3 = fract(vec3(s2.xyx) * 0.1031);
	q3 += dot(q3, q3.yzx + 33.33);
	vec2 b = fract((q3.xx + q3.yz) * q3.zy);
	return vec4(a, b);
}

vec2 poiRand2(float seed)
{
	vec2 x = vec2(seed, seed * 1.3);
	vec3 p3 = fract(vec3(x.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

vec2 poiRand2(vec2 seed)
{
	vec3 p3 = fract(vec3(seed.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

float poiRand3(float seed)
{
	float p = fract(seed * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

vec3 poiRand3(vec2 seed)
{
	vec3 p3 = fract(vec3(seed.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xxy + p3.yzz) * p3.zyx);
}

vec3 poiRand3(vec3 seed)
{
	vec3 p3 = fract(seed * 0.1031);
	p3 += dot(p3, p3.zyx + 31.32);
	return fract((p3.xxy + p3.yzz) * p3.zyx);
}

vec3 poiRand3Range(vec2 Seed, float Range)
{
	vec3 r = poiRand3(Seed);
	return (r * 2.0 - 1.0) * Range;
}

// Deprecated: use poiRand3Range instead of randomFloat3Range
vec3 randomFloat3WiggleRange(vec2 Seed, float Range, float wiggleSpeed, float timeOffset)
{
	vec3 rando = (vec3(
	fract(sin(dot(Seed.xy, vec2(12.9898, 78.233))) * 43758.5453),
	fract(sin(dot(Seed.yx, vec2(12.9898, 78.233))) * 43758.5453),
	fract(sin(dot(vec2(Seed.x * Seed.y, Seed.y + Seed.x), vec2(12.9898, 78.233))) * 43758.5453)
	) * 2.0 - 1.0);
	float speed = 1.0 + wiggleSpeed;
	return vec3(sin(((POI_TIME.x + timeOffset) + rando.x * PI) * speed), sin(((POI_TIME.x + timeOffset) + rando.y * PI) * speed), sin(((POI_TIME.x + timeOffset) + rando.z * PI) * speed)) * Range;
}

// The weights of RGB contributions to luminance.
// Should sum to unity.
const vec3 HCYwts = vec3(0.299, 0.587, 0.114);
const float HCLgamma = 3.0;
const float HCLy0 = 100.0;
const float HCLmaxL = 0.530454533953517; // == exp(HCLgamma / HCLy0) - 0.5
const vec3 wref = vec3(1.0, 1.0, 1.0);

vec3 HUEtoRGB(in float H)
{
	float R = abs(H * 6.0 - 3.0) - 1.0;
	float G = 2.0 - abs(H * 6.0 - 2.0);
	float B = 2.0 - abs(H * 6.0 - 4.0);
	return clamp(vec3(R, G, B), 0.0, 1.0);
}

vec3 RGBtoHCV(in vec3 RGB)
{
	// Based on work by Sam Hocevar and Emil Persson
	vec4 P = (RGB.g < RGB.b) ? vec4(RGB.bg, -1.0, 2.0 / 3.0) : vec4(RGB.gb, 0.0, -1.0 / 3.0);
	vec4 Q = (RGB.r < P.x) ? vec4(P.xyw, RGB.r) : vec4(RGB.r, P.yzx);
	float C = Q.x - min(Q.w, Q.y);
	float H = abs((Q.w - Q.y) / (6.0 * C + EPSILON) + Q.z);
	return vec3(H, C, Q.x);
}

vec3 RGBtoHSV(vec3 c)
{
	vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
	vec4 p = mix(vec4(c.bg, K.wz), vec4(c.gb, K.xy), step(c.b, c.g));
	vec4 q = mix(vec4(p.xyw, c.r), vec4(c.r, p.yzx), step(p.x, c.r));

	float d = q.x - min(q.w, q.y);
	const float e = 1.0e-4;
	return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

vec3 HSVtoRGB(vec3 c)
{
	vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
	vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
	return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

void DecomposeHDRColor(in vec3 linearColorHDR, out vec3 baseLinearColor, out float exposure)
{
	// Optimization/adaptation of https://github.com/Unity-Technologies/UnityCsReference/blob/master/Editor/Mono/GUI/ColorMutator.cs#L23 but skips weird photoshop stuff
	float maxColorComponent = max(linearColorHDR.r, max(linearColorHDR.g, linearColorHDR.b));
	bool isSDR = maxColorComponent <= 1.0;

	float scaleFactor = isSDR ? 1.0 : (1.0 / maxColorComponent);
	exposure = isSDR ? 0.0 : log(maxColorComponent) * 1.44269504089; // ln(2)

	baseLinearColor = scaleFactor * linearColorHDR;
}

vec3 ApplyHDRExposure(vec3 linearColor, float exposure)
{
	return linearColor * pow(2.0, exposure);
}

// Transforms an RGB color using a matrix. Note that S and V are absolute values here
vec3 ModifyViaHSV(vec3 color, float h, float s, float v)
{
	vec3 colorHSV = RGBtoHSV(color);
	colorHSV.x = fract(colorHSV.x + h);
	colorHSV.y = clamp(colorHSV.y + s, 0.0, 1.0);
	colorHSV.z = clamp(colorHSV.z + v, 0.0, 1.0);
	return HSVtoRGB(colorHSV);
}

vec3 ModifyViaHSV(vec3 color, vec3 HSVMod)
{
	return ModifyViaHSV(color, HSVMod.x, HSVMod.y, HSVMod.z);
}

// Matrices are written row by row as in the source and transposed into place; colors multiply as row vectors.
mat4 brightnessMatrix(float brightness)
{
	return transpose(mat4(
	vec4(1.0, 0.0, 0.0, 0.0),
	vec4(0.0, 1.0, 0.0, 0.0),
	vec4(0.0, 0.0, 1.0, 0.0),
	vec4(brightness, brightness, brightness, 1.0)
	));
}

mat4 contrastMatrix(float contrast)
{
	float t = (1.0 - contrast) / 2.0;

	return transpose(mat4(
	vec4(contrast, 0.0, 0.0, 0.0),
	vec4(0.0, contrast, 0.0, 0.0),
	vec4(0.0, 0.0, contrast, 0.0),
	vec4(t, t, t, 1.0)
	));
}

mat4 saturationMatrix(float saturation)
{
	vec3 luminance = vec3(0.3086, 0.6094, 0.0820);

	float oneMinusSat = 1.0 - saturation;

	vec3 red = vec3(luminance.x * oneMinusSat);
	red += vec3(saturation, 0.0, 0.0);

	vec3 green = vec3(luminance.y * oneMinusSat);
	green += vec3(0.0, saturation, 0.0);

	vec3 blue = vec3(luminance.z * oneMinusSat);
	blue += vec3(0.0, 0.0, saturation);

	return transpose(mat4(
	vec4(red, 0.0),
	vec4(green, 0.0),
	vec4(blue, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0)
	));
}

vec4 PoiColorBCS(vec4 color, float brightness, float contrast, float saturation)
{
	return color * (brightnessMatrix(brightness) * (contrastMatrix(contrast) * saturationMatrix(saturation)));
}

vec3 PoiColorBCS(vec3 color, float brightness, float contrast, float saturation)
{
	return (vec4(color, 1.0) * (brightnessMatrix(brightness) * (contrastMatrix(contrast) * saturationMatrix(saturation)))).rgb;
}

vec3 linear_srgb_to_oklab(vec3 c)
{
	float l = 0.4122214708 * c.x + 0.5363325363 * c.y + 0.0514459929 * c.z;
	float m = 0.2119034982 * c.x + 0.6806995451 * c.y + 0.1073969566 * c.z;
	float s = 0.0883024619 * c.x + 0.2817188376 * c.y + 0.6299787005 * c.z;

	float l_ = pow(l, 1.0 / 3.0);
	float m_ = pow(m, 1.0 / 3.0);
	float s_ = pow(s, 1.0 / 3.0);

	return vec3(
	0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
	1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
	0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
	);
}

vec3 oklab_to_linear_srgb(vec3 c)
{
	float l_ = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
	float m_ = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
	float s_ = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;

	float l = l_ * l_ * l_;
	float m = m_ * m_ * m_;
	float s = s_ * s_ * s_;

	return vec3(
	+4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
	-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
	-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
	);
}

vec3 PoiApplyChromatize(vec3 linearColor, float chromatize)
{
	float minRgb = min(linearColor.r, min(linearColor.g, linearColor.b));
	float maxRgb = max(linearColor.r, max(linearColor.g, linearColor.b));
	float rangeRgb = maxRgb - minRgb;
	vec3 saturatedRef = (rangeRgb > 0.0001) ? (linearColor - minRgb) / rangeRgb : vec3(1.0, 0.0, 0.0);
	vec3 saturatedOklab = linear_srgb_to_oklab(saturatedRef);
	float maxChroma = length(saturatedOklab.yz);

	vec3 oklab = linear_srgb_to_oklab(linearColor);
	float chroma = length(oklab.yz);
	float hue = atan(oklab.z, oklab.y);
	chroma = min(chroma * (1.0 + chromatize), maxChroma);
	oklab.y = chroma * cos(hue);
	oklab.z = chroma * sin(hue);
	return oklab_to_linear_srgb(oklab);
}

vec3 hueShiftOKLab(vec3 color, float shift, float selectOrShift)
{
	vec3 oklab = linear_srgb_to_oklab(color);
	float chroma = length(oklab.yz);

	oklab.y = selectOrShift > 0.5 ? oklab.y : chroma;
	oklab.z = selectOrShift > 0.5 ? oklab.z : 0.0;
	float s = sin(shift * TAU);
	float c = cos(shift * TAU);
	oklab.yz = vec2(c * oklab.y - s * oklab.z, s * oklab.y + c * oklab.z);

	return oklab_to_linear_srgb(oklab);
}

vec3 hueShiftHSV(vec3 color, float hueOffset, float selectOrShift)
{
	vec3 hsvCol = RGBtoHSV(color);
	hsvCol.x = hsvCol.x * selectOrShift + hueOffset;
	return HSVtoRGB(hsvCol);
}

vec3 hueShift(vec3 color, float shift, float ColorSpace, float selectOrShift)
{
	float w = clamp(ColorSpace, 0.0, 1.0);
	if (w <= 0.0)
	{
		return hueShiftOKLab(color, shift, selectOrShift);
	}
	if (w >= 1.0)
	{
		return hueShiftHSV(color, shift, selectOrShift);
	}
	return mix(hueShiftOKLab(color, shift, selectOrShift), hueShiftHSV(color, shift, selectOrShift), w);
}

vec3 hueShift(vec3 color, float shift, float ColorSpace)
{
	return hueShift(color, shift, ColorSpace, 0.0);
}

vec4 hueShift(vec4 color, float shift, float ColorSpace, float selectOrShift)
{
	return vec4(hueShift(color.rgb, shift, ColorSpace, selectOrShift), color.a);
}

vec4 hueShift(vec4 color, float shift, float ColorSpace)
{
	return hueShift(color, shift, ColorSpace, 0.0);
}

mat4 poiRotationMatrixFromAngles(float x, float y, float z)
{
	float angleX = radians(x);
	float c = cos(angleX);
	float s = sin(angleX);
	mat4 rotateXMatrix = transpose(mat4(
	vec4(1.0, 0.0, 0.0, 0.0),
	vec4(0.0, c, -s, 0.0),
	vec4(0.0, s, c, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0)));

	float angleY = radians(y);
	c = cos(angleY);
	s = sin(angleY);
	mat4 rotateYMatrix = transpose(mat4(
	vec4(c, 0.0, s, 0.0),
	vec4(0.0, 1.0, 0.0, 0.0),
	vec4(-s, 0.0, c, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0)));

	float angleZ = radians(z);
	c = cos(angleZ);
	s = sin(angleZ);
	mat4 rotateZMatrix = transpose(mat4(
	vec4(c, -s, 0.0, 0.0),
	vec4(s, c, 0.0, 0.0),
	vec4(0.0, 0.0, 1.0, 0.0),
	vec4(0.0, 0.0, 0.0, 1.0)));

	return (rotateXMatrix * rotateYMatrix) * rotateZMatrix;
}

mat4 poiRotationMatrixFromAngles(vec3 angles)
{
	return poiRotationMatrixFromAngles(angles.x, angles.y, angles.z);
}

// Without useCenterEye this is the current eye, read from the view so it stays right after the fragment
// setup moves poiCam.worldPos to the centre eye.
vec3 getCameraPosition(in PoiCam poiCam, bool useCenterEye)
{
	return useCenterEye ? poiCam.centerEyePos : poiCam.invViewMatrix[3].xyz;
}

vec3 getCameraPosition(in PoiCam poiCam)
{
	return getCameraPosition(poiCam, true);
}

vec2 calcPixelScreenUVs(in PoiCam poiCam, vec4 grabPos)
{
	vec2 uv = grabPos.xy / (grabPos.w + 0.0000000001);
	uv.xy *= poiCam.screenSize;

	return uv;
}

float CalcMipLevel(vec2 texture_coord)
{
	vec2 dx = dFdx(texture_coord);
	vec2 dy = dFdy(texture_coord);
	float delta_max_sqr = max(dot(dx, dx), dot(dy, dy));

	return 0.5 * log2(delta_max_sqr);
}

float inverseLerp(float A, float B, float T)
{
	return (T - A) / (B - A);
}

float inverseLerp2(vec2 a, vec2 b, vec2 value)
{
	vec2 AB = b - a;
	vec2 AV = value - a;
	return dot(AV, AB) / dot(AB, AB);
}

float inverseLerp3(vec3 a, vec3 b, vec3 value)
{
	vec3 AB = b - a;
	vec3 AV = value - a;
	return dot(AV, AB) / dot(AB, AB);
}

float inverseLerp4(vec4 a, vec4 b, vec4 value)
{
	vec4 AB = b - a;
	vec4 AV = value - a;
	return dot(AV, AB) / dot(AB, AB);
}

vec4 QuaternionFromMatrix(
float m00, float m01, float m02,
float m10, float m11, float m12,
float m20, float m21, float m22)
{
	vec4 q = vec4(0.0);
	float trace = m00 + m11 + m22;
	if (trace > 0.0)
	{
		float s = sqrt(trace + 1.0) * 2.0;
		q.w = 0.25 * s;
		q.x = (m21 - m12) / s;
		q.y = (m02 - m20) / s;
		q.z = (m10 - m01) / s;
	}
	else if (m00 > m11 && m00 > m22)
	{
		float s = sqrt(1.0 + m00 - m11 - m22) * 2.0;
		q.w = (m21 - m12) / s;
		q.x = 0.25 * s;
		q.y = (m01 + m10) / s;
		q.z = (m02 + m20) / s;
	}
	else if (m11 > m22)
	{
		float s = sqrt(1.0 + m11 - m00 - m22) * 2.0;
		q.w = (m02 - m20) / s;
		q.x = (m01 + m10) / s;
		q.y = 0.25 * s;
		q.z = (m12 + m21) / s;
	}
	else
	{
		float s = sqrt(1.0 + m22 - m00 - m11) * 2.0;
		q.w = (m10 - m01) / s;
		q.x = (m02 + m20) / s;
		q.y = (m12 + m21) / s;
		q.z = 0.25 * s;
	}
	return q;
}

vec4 MulQuat(vec4 a, vec4 b)
{
	return vec4(
	a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
	a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
	a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
	a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z
	);
}

vec4 QuaternionFromBasis(vec3 sx, vec3 sy, vec3 sz)
{
	return QuaternionFromMatrix(
	sx.x, sy.x, sz.x,
	sx.y, sy.y, sz.y,
	sx.z, sy.z, sz.z
	);
}

vec4 BuildQuatFromForwardUp(vec3 forward, vec3 up)
{
	vec3 f = normalize(forward);
	vec3 u = normalize(up);
	vec3 x = normalize(cross(u, f));
	vec3 y = cross(f, x);
	return QuaternionFromBasis(x, y, f);
}

vec3 QuaternionToEuler(vec4 q)
{
	vec3 euler = vec3(0.0);

	float sinr_cosp = 2.0 * (q.w * q.z + q.x * q.y);
	float cosr_cosp = 1.0 - 2.0 * (q.z * q.z + q.x * q.x);
	euler.z = atan(sinr_cosp, cosr_cosp) * 57.2958;

	float sinp = 2.0 * (q.w * q.x - q.y * q.z);
	if (abs(sinp) >= 1.0)
	euler.x = (sinp >= 0.0 ? 1.0 : -1.0) * 90.0;
	else
	euler.x = asin(sinp) * 57.2958;

	float siny_cosp = 2.0 * (q.w * q.y + q.z * q.x);
	float cosy_cosp = 1.0 - 2.0 * (q.x * q.x + q.y * q.y);
	euler.y = atan(siny_cosp, cosy_cosp) * 57.2958;

	return euler;
}

vec4 EulerToQuaternion(vec3 euler)
{
	vec3 eulerRad = euler * 0.0174533;

	float cx = cos(eulerRad.x * 0.5);
	float sx = sin(eulerRad.x * 0.5);
	float cy = cos(eulerRad.y * 0.5);
	float sy = sin(eulerRad.y * 0.5);
	float cz = cos(eulerRad.z * 0.5);
	float sz = sin(eulerRad.z * 0.5);

	vec4 q = vec4(0.0);
	q.w = cx * cy * cz + sx * sy * sz;
	q.x = sx * cy * cz - cx * sy * sz;
	q.y = cx * sy * cz + sx * cy * sz;
	q.z = cx * cy * sz - sx * sy * cz;

	return q;
}

/*
MIT License

Copyright (c) 2019 wraikny

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

VertexTransformShader is dependent on:
*/

vec4 quaternion_conjugate(vec4 v)
{
	return vec4(
	v.x, -v.yzw
	);
}

vec4 quaternion_mul(vec4 v1, vec4 v2)
{
	vec4 result1 = (v1.x * v2 + v1 * v2.x);

	vec4 result2 = vec4(
	-dot(v1.yzw, v2.yzw),
	cross(v1.yzw, v2.yzw)
	);

	return vec4(result1 + result2);
}

// angle : radians
vec4 get_quaternion_from_angle(vec3 axis, float angle)
{
	float sn = sin(angle * 0.5);
	float cs = cos(angle * 0.5);
	return vec4(axis * sn, cs);
}

vec4 quaternion_from_vector(vec3 inVec)
{
	return vec4(0.0, inVec);
}

float degree_to_radius(float degree)
{
	return (
	degree / 180.0 * PI
	);
}

#define MUL3(A, B, C) quaternion_mul(quaternion_mul((A), (B)), (C))

vec3 rotate_with_quaternion(vec3 inVec, vec3 rotation)
{
	vec4 qx = get_quaternion_from_angle(vec3(1.0, 0.0, 0.0), radians(rotation.x));
	vec4 qy = get_quaternion_from_angle(vec3(0.0, 1.0, 0.0), radians(rotation.y));
	vec4 qz = get_quaternion_from_angle(vec3(0.0, 0.0, 1.0), radians(rotation.z));

	vec4 quaternion = normalize(MUL3(qx, qy, qz));
	vec4 conjugate = quaternion_conjugate(quaternion);

	vec4 inVecQ = quaternion_from_vector(inVec);

	vec3 rotated = (
	MUL3(quaternion, inVecQ, conjugate)
	).yzw;

	return rotated;
}

vec3 RotateByQuaternion(vec4 q, vec3 v)
{
	vec3 u = q.xyz;
	float s = q.w;
	return 2.0 * dot(u, v) * u
	+ (s * s - dot(u, u)) * v
	+ 2.0 * s * cross(u, v);
}

vec4 SlerpQuaternion(vec4 qa, vec4 qb, float t)
{
	float cosHalfTheta = dot(qa, qb);
	// take shortest path
	if (cosHalfTheta < 0.0)
	{
		qb = -qb;
		cosHalfTheta = -cosHalfTheta;
	}
	if (cosHalfTheta > 0.9995)
	{
		// almost parallel, use lerp
		vec4 qr = normalize(qa * (1.0 - t) + qb * t);
		return qr;
	}
	float halfTheta = acos(cosHalfTheta);
	float sinHalfTheta = sqrt(1.0 - cosHalfTheta * cosHalfTheta);
	float a = sin((1.0 - t) * halfTheta) / sinHalfTheta;
	float b = sin(t * halfTheta) / sinHalfTheta;
	return qa * a + qb * b;
}

vec4 transform(vec4 input_, vec4 pos, vec4 rotation, vec4 scale)
{
	input_.rgb *= (scale.xyz * scale.w);
	input_ = vec4(rotate_with_quaternion(input_.xyz, rotation.xyz * rotation.w) + (pos.xyz * pos.w), input_.w);
	return input_;
}
/*
MIT END
*/

vec2 RotateUV(vec2 _uv, float _radian, vec2 _piv, float _time)
{
	float RotateUV_ang = _radian;
	float RotateUV_cos = cos(_time * RotateUV_ang);
	float RotateUV_sin = sin(_time * RotateUV_ang);
	return ((_uv - _piv) * transpose(mat2(vec2(RotateUV_cos, -RotateUV_sin), vec2(RotateUV_sin, RotateUV_cos))) + _piv);
}

vec3 RotateAroundAxis(vec3 original, vec3 axis, float radian)
{
	float s = sin(radian);
	float c = cos(radian);
	float one_minus_c = 1.0 - c;

	axis = normalize(axis);
	mat3 rot_mat = transpose(mat3(
		vec3(one_minus_c * axis.x * axis.x + c, one_minus_c * axis.x * axis.y - axis.z * s, one_minus_c * axis.z * axis.x + axis.y * s),
		vec3(one_minus_c * axis.x * axis.y + axis.z * s, one_minus_c * axis.y * axis.y + c, one_minus_c * axis.y * axis.z - axis.x * s),
		vec3(one_minus_c * axis.z * axis.x - axis.y * s, one_minus_c * axis.y * axis.z + axis.x * s, one_minus_c * axis.z * axis.z + c)
	));
	return rot_mat * original;
}

vec3 lilToneCorrection(vec3 c, vec4 hsvg)
{
	// gamma
	c = pow(abs(c), vec3(hsvg.w));
	// rgb - > hsv
	vec4 p = (c.b > c.g) ? vec4(c.bg, -1.0, 2.0 / 3.0) : vec4(c.gb, 0.0, -1.0 / 3.0);
	vec4 q = (p.x > c.r) ? vec4(p.xyw, c.r) : vec4(c.r, p.yzx);
	float d = q.x - min(q.w, q.y);
	const float e = 1.0e-4;
	vec3 hsv = vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
	// shift
	hsv = vec3(hsv.x + hsvg.x, clamp(hsv.y * hsvg.y, 0.0, 1.0), clamp(hsv.z * hsvg.z, 0.0, 1.0));
	// hsv - > rgb
	return hsv.z - hsv.z * hsv.y + hsv.z * hsv.y * clamp(abs(fract(hsv.x + vec3(1.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
}

vec3 lilBlendColor(vec3 dstCol, vec3 srcCol, vec3 srcA, int blendMode)
{
	vec3 ad = dstCol + srcCol;
	vec3 mu = dstCol * srcCol;
	vec3 outCol = vec3(0.0, 0.0, 0.0);
	if (blendMode == 0) outCol = srcCol; // Normal
	if (blendMode == 1) outCol = ad; // Add
	if (blendMode == 2) outCol = max(ad - mu, dstCol); // Screen
	if (blendMode == 3) outCol = mu; // Multiply
	return mix(dstCol, outCol, srcA);
}

vec3 lilBlendColor(vec3 dstCol, vec3 srcCol, vec3 srcA, float blendMode)
{
	return lilBlendColor(dstCol, srcCol, srcA, int(blendMode));
}

vec3 lilBlendColor(vec3 dstCol, vec3 srcCol, float srcA, int blendMode)
{
	return lilBlendColor(dstCol, srcCol, vec3(srcA), blendMode);
}

vec3 lilBlendColor(vec3 dstCol, vec3 srcCol, float srcA, float blendMode)
{
	return lilBlendColor(dstCol, srcCol, vec3(srcA), int(blendMode));
}

float lilIsIn0to1(float f)
{
	float value = 0.5 - abs(f - 0.5);
	return clamp(value / clamp(fwidth(value), 0.0001, 1.0), 0.0, 1.0);
}

float lilIsIn0to1(float f, float nv)
{
	float value = 0.5 - abs(f - 0.5);
	return clamp(value / clamp(fwidth(value), 0.0001, nv), 0.0, 1.0);
}

float poiEdgeLinearNoSaturate(float value, float border)
{
	return (value - border) / clamp(fwidth(value), 0.0001, 1.0);
}

vec3 poiEdgeLinearNoSaturate(float value, vec3 border)
{
	return vec3(
	(value - border.x) / clamp(fwidth(value), 0.0001, 1.0),
	(value - border.y) / clamp(fwidth(value), 0.0001, 1.0),
	(value - border.z) / clamp(fwidth(value), 0.0001, 1.0)
	);
}

float poiEdgeLinearNoSaturate(float value, float border, float blur)
{
	float borderMin = clamp(border - blur * 0.5, 0.0, 1.0);
	float borderMax = clamp(border + blur * 0.5, 0.0, 1.0);
	return (value - borderMin) / max(clamp(borderMax - borderMin + fwidth(value), 0.0, 1.0), 0.0001);
}

float poiEdgeLinearNoSaturate(float value, float border, float blur, float borderRange)
{
	float borderMin = clamp(border - blur * 0.5 - borderRange, 0.0, 1.0);
	float borderMax = clamp(border + blur * 0.5, 0.0, 1.0);
	return (value - borderMin) / max(clamp(borderMax - borderMin + fwidth(value), 0.0, 1.0), 0.0001);
}

float poiEdgeNonLinearNoSaturate(float value, float border)
{
	float fwidthValue = fwidth(value);
	return smoothstep(border - fwidthValue, border + fwidthValue, value);
}

float poiEdgeNonLinearNoSaturate(float value, float border, float blur)
{
	float fwidthValue = fwidth(value);
	float borderMin = clamp(border - blur * 0.5, 0.0, 1.0);
	float borderMax = clamp(border + blur * 0.5, 0.0, 1.0);
	return smoothstep(borderMin - fwidthValue, borderMax + fwidthValue, value);
}

float poiEdgeNonLinearNoSaturate(float value, float border, float blur, float borderRange)
{
	float fwidthValue = fwidth(value);
	float borderMin = clamp(border - blur * 0.5 - borderRange, 0.0, 1.0);
	float borderMax = clamp(border + blur * 0.5, 0.0, 1.0);
	return smoothstep(borderMin - fwidthValue, borderMax + fwidthValue, value);
}

float poiEdgeNonLinear(float value, float border)
{
	return clamp(poiEdgeNonLinearNoSaturate(value, border), 0.0, 1.0);
}

float poiEdgeNonLinear(float value, float border, float blur)
{
	return clamp(poiEdgeNonLinearNoSaturate(value, border, blur), 0.0, 1.0);
}

float poiEdgeNonLinear(float value, float border, float blur, float borderRange)
{
	return clamp(poiEdgeNonLinearNoSaturate(value, border, blur, borderRange), 0.0, 1.0);
}

float poiEdgeLinear(float value, float border)
{
	return clamp(poiEdgeLinearNoSaturate(value, border), 0.0, 1.0);
}

float poiEdgeLinear(float value, float border, float blur)
{
	return clamp(poiEdgeLinearNoSaturate(value, border, blur), 0.0, 1.0);
}

float poiEdgeLinear(float value, float border, float blur, float borderRange)
{
	return clamp(poiEdgeLinearNoSaturate(value, border, blur, borderRange), 0.0, 1.0);
}

// From https : // github.com / lilxyzw / OpenLit / blob / main / Assets / OpenLit / core.hlsl
vec3 OpenLitLinearToSRGB(vec3 col)
{
	return LinearToGammaSpace(col);
}

vec3 OpenLitSRGBToLinear(vec3 col)
{
	return GammaToLinearSpace(col);
}

float OpenLitLuminance(vec3 rgb)
{
	return dot(rgb, vec3(0.0396819152, 0.458021790, 0.00609653955));
}

vec3 AdjustLitLuminance(vec3 rgb, float targetLuminance)
{
	float currentLuminance = dot(rgb, vec3(0.0396819152, 0.458021790, 0.00609653955));

	float luminanceRatio = targetLuminance / currentLuminance;
	return rgb * luminanceRatio;
}

vec3 ClampLuminance(vec3 rgb, float minLuminance, float maxLuminance)
{
	float currentLuminance = dot(rgb, vec3(0.299, 0.587, 0.114));
	float minRatio = (currentLuminance != 0.0) ? minLuminance / currentLuminance : 1.0;
	float maxRatio = (currentLuminance != 0.0) ? maxLuminance / currentLuminance : 1.0;
	float luminanceRatio = clamp(min(maxRatio, max(minRatio, 1.0)), 0.0, 1.0);
	return mix(rgb, rgb * luminanceRatio, float(luminanceRatio < 1.0));
}

vec3 MaxLuminance(vec3 rgb, float maxLuminance)
{
	float currentLuminance = dot(rgb, vec3(0.299, 0.587, 0.114));
	float luminanceRatio = (currentLuminance != 0.0) ? maxLuminance / max(currentLuminance, 0.00001) : 1.0;
	return mix(rgb, rgb * luminanceRatio, float(currentLuminance > maxLuminance));
}

float OpenLitGray(vec3 rgb)
{
	return dot(rgb, vec3(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0));
}

vec3 OpenLitComputeCustomLightDirection(in PoiMesh poiMesh, vec4 lightDirectionOverride)
{
	vec3 customDir = length(lightDirectionOverride.xyz) * normalize(mat3(poiMesh.modelMatrix) * lightDirectionOverride.xyz);
	return lightDirectionOverride.w != 0.0 ? customDir : lightDirectionOverride.xyz; // .w isn't doc'd anywhere and is always 0 unless end user changes it
}

// No SH here: the source's lightmapped branch, with the SH direction at zero.
vec3 OpenLitLightingDirectionForSH9(vec3 mainDir)
{
	vec3 sh9Dir = vec3(0.0);

	vec3 lightDirectionForSH9 = sh9Dir + mainDir;
	lightDirectionForSH9 = dot(lightDirectionForSH9, lightDirectionForSH9) < 0.000001 ? vec3(0.0) : normalize(lightDirectionForSH9);
	return lightDirectionForSH9;
}

vec3 OpenLitLightingDirection(in PoiMesh poiMesh, vec4 lightDirectionOverride, vec3 mainDir)
{
	vec3 sh9DirAbs = vec3(0.0);
	vec3 customDir = OpenLitComputeCustomLightDirection(poiMesh, lightDirectionOverride);

	return normalize(sh9DirAbs + mainDir + customDir);
}

// Unity's projection terms by row and column (_rc), read from the column-major matrix as [c][r].
vec4 CalculateFrustumCorrection(in PoiCam poiCam)
{
	mat4 P = poiCam.projectionMatrix;
	float x1 = -P[0][2] / (P[0][0] * P[3][2]);
	float x2 = -P[1][2] / (P[1][1] * P[3][2]);
	return vec4(x1, x2, 0.0, P[2][2] / P[3][2] + x1 * P[2][0] + x2 * P[2][1]);
}

float CorrectedLinearEyeDepth(in PoiCam poiCam, float z, float correctionFactor)
{
	return 1.0 / (z / poiCam.projectionMatrix[3][2] + correctionFactor);
}

float GetFOVScale(in PoiCam poiCam)
{
	return abs(poiCam.projectionMatrix[1][1]) * 0.5;
}

float GetAspectRatio(in PoiCam poiCam)
{
	return poiCam.screenSize.y / poiCam.screenSize.x;
}

float WorldToScreenScale(in PoiCam poiCam, float worldRadius, float distance)
{
	return (worldRadius / max(distance, 0.001)) * GetFOVScale(poiCam);
}

vec2 GetScreenAspectCorrection(in PoiCam poiCam)
{
	return vec2(poiCam.screenSize.y / poiCam.screenSize.x, 1.0);
}

float evalRamp4(float time, vec4 ramp)
{
	return mix(ramp.x, ramp.y, smoothstep(ramp.z, ramp.w, time));
}

// Silent's code
vec2 sharpSample(vec4 texelSize, vec2 p)
{
	p = p * texelSize.zw;
	vec2 c = max(vec2(0.0), fwidth(p));
	p = floor(p) + clamp(fract(p) / c, 0.0, 1.0);
	p = (p - 0.5) * texelSize.xy;
	return p;
}

// Quantize a UV to the center of its cell on a resolution x resolution grid.
// Pair with poiSnapUVGradients + SampleGrad.
vec2 poiSnapUVToGrid(vec2 uv, vec2 resolution)
{
	return (floor(uv * resolution) + 0.5) / resolution;
}

void poiSnapUVGradients(vec2 continuousUV, vec2 cellSize, out vec2 dx, out vec2 dy)
{
	dx = max(abs(dFdx(continuousUV)), vec2(cellSize.x, 0.0));
	dy = max(abs(dFdy(continuousUV)), vec2(0.0, cellSize.y));
}

// Quantize sampleUV to a grid that lives in a different UV space (gridUV). The grid is quantized in gridUV
// and the screen-space Jacobian dSample/dGrid carries the sample to that cell's centre, so view-dependent
// UVs (panosphere, matcap, screen space) stay welded to the surface. Exact for flat surfaces; where gridUV
// does not vary across the quad it snaps in place. cellFootprint is how much of sampleUV one cell spans,
// for the gradient floor that picks the mip.
vec2 poiSnapUVToForeignGrid(vec2 sampleUV, vec2 gridUV, vec2 resolution, out vec2 cellFootprint)
{
	// Every derivative is taken before the branch below; ddx/ddy under divergent flow is undefined.
	vec2 dGdx = dFdx(gridUV);
	vec2 dGdy = dFdy(gridUV);
	vec2 dSdx = dFdx(sampleUV);
	vec2 dSdy = dFdy(sampleUV);

	float det = dGdx.x * dGdy.y - dGdy.x * dGdx.y;
	if (abs(det) < 1e-12)
	{
		cellFootprint = 1.0 / resolution;
		return poiSnapUVToGrid(sampleUV, resolution);
	}

	vec2 toCellCenter = poiSnapUVToGrid(gridUV, resolution) - gridUV;

	float rcpDet = 1.0 / det;
	vec2 dSdu = (dGdy.y * dSdx - dGdx.y * dSdy) * rcpDet;
	vec2 dSdv = (dGdx.x * dSdy - dGdy.x * dSdx) * rcpDet;

	// One cell steps 1/resolution along each grid axis; both axes can move both components of
	// sampleUV, so the footprint is their summed magnitude.
	cellFootprint = abs(dSdu) / resolution.x + abs(dSdv) / resolution.y;

	return sampleUV + toCellCenter.x * dSdu + toCellCenter.y * dSdv;
}

void applyToGlobalMask(inout PoiMods poiMods, int index, int blendType, float val)
{
	float i = float(index);
	float w[16];
	float cur = 0.0;
	for (int k = 0; k < 16; k++)
	{
		w[k] = clamp(1.0 - abs(i - float(k)), 0.0, 1.0);
		cur += poiMods.globalMask[k] * w[k];
	}
	float nv = clamp(maskBlend(cur, val, float(blendType)), 0.0, 1.0);
	for (int k = 0; k < 16; k++)
	{
		poiMods.globalMask[k] = mix(poiMods.globalMask[k], nv, w[k]);
	}
}

// The source's int parameters truncate the float uniforms callers pass.
void applyToGlobalMask(inout PoiMods poiMods, float index, float blendType, float val)
{
	applyToGlobalMask(poiMods, int(index), int(blendType), val);
}

void assignValueToVectorFromIndex(inout vec4 vec_, int index, float value)
{
	float i = float(clamp(index, 0, 3));
	float w0 = clamp(1.0 - abs(i - 0.0), 0.0, 1.0);
	float w1 = clamp(1.0 - abs(i - 1.0), 0.0, 1.0);
	float w2 = clamp(1.0 - abs(i - 2.0), 0.0, 1.0);
	float w3 = clamp(1.0 - abs(i - 3.0), 0.0, 1.0);
	vec_[0] = mix(vec_[0], value, w0);
	vec_[1] = mix(vec_[1], value, w1);
	vec_[2] = mix(vec_[2], value, w2);
	vec_[3] = mix(vec_[3], value, w3);
}

void assignValueToVectorFromIndex(inout vec4 vec_, float index, float value)
{
	assignValueToVectorFromIndex(vec_, int(index), value);
}

// SNose
vec3 mod289(vec3 x)
{
	return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec2 mod289(vec2 x)
{
	return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec3 permute(vec3 x)
{
	return mod289(((x * 34.0) + 1.0) * x);
}

float snoise(vec2 v)
{
	const vec4 C = vec4(0.211324865405187, // (3.0 - sqrt(3.0)) / 6.0
	0.366025403784439, // 0.5 * (sqrt(3.0) - 1.0)
	-0.577350269189626, // - 1.0 + 2.0 * C.x
	0.024390243902439); // 1.0 / 41.0
	vec2 i = floor(v + dot(v, C.yy));
	vec2 x0 = v - i + dot(i, C.xx);
	vec2 i1 = vec2(0.0);
	i1 = (x0.x > x0.y) ? vec2(1.0, 0.0) : vec2(0.0, 1.0);
	vec4 x12 = x0.xyxy + C.xxzz;
	x12.xy -= i1;
	i = mod289(i); // Avoid truncation effects in permutation
	vec3 p = permute(permute(i.y + vec3(0.0, i1.y, 1.0))
	+ i.x + vec3(0.0, i1.x, 1.0));

	vec3 m = max(0.5 - vec3(dot(x0, x0), dot(x12.xy, x12.xy), dot(x12.zw, x12.zw)), vec3(0.0));
	m = m * m;
	m = m * m;
	vec3 x = 2.0 * fract(p * C.www) - 1.0;
	vec3 h = abs(x) - 0.5;
	vec3 ox = floor(x + 0.5);
	vec3 a0 = x - ox;
	m *= 1.79284291400159 - 0.85373472095314 * (a0 * a0 + h * h);
	vec3 g = vec3(0.0);
	g.x = a0.x * x0.x + h.x * x0.y;
	g.yz = a0.yz * x12.xz + h.yz * x12.yw;
	return 130.0 * dot(m, g);
}

// 8x8 ordered (Bayer) dither in [0,1). Bit/sin-free -> Quest/Adreno safe.
float PoiBayer2(vec2 a) { a = floor(a); return fract(a.x * 0.5 + a.y * a.y * 0.75); }
float PoiBayer8(vec2 a) { return PoiBayer2(a * 0.25) * 0.0625 + PoiBayer2(a * 0.5) * 0.25 + PoiBayer2(a); }

float poiInvertToggle(in float value, in float toggle)
{
	return (toggle == 0.0 ? value : 1.0 - value);
}

vec3 PoiBlendNormal(vec3 dstNormal, vec3 srcNormal)
{
	return vec3(dstNormal.xy + srcNormal.xy, dstNormal.z * srcNormal.z);
}

vec3 lilTransformDirOStoWS(in PoiMesh poiMesh, vec3 directionOS, bool doNormalize)
{
	if (doNormalize) return normalize(mat3(poiMesh.modelMatrix) * directionOS);
	else            return mat3(poiMesh.modelMatrix) * directionOS;
}

// sampler2D or sampler2DArray.
#define poiGetWidthAndHeight(tex) vec2(textureSize(tex, 0).xy)

// There is always a radiance source (sky, probe or flat ambient).
// An omni or spot light the shading left black. The matcap light hook adds such a light's share times its
// finalLighting, so there it has nothing to add and skips the work. -xlinka
bool poiLightAddsNoShare(in PoiLight poiLight)
{
	return !poiLight.isDirectional && all(equal(poiLight.finalLighting, vec3(0.0)));
}

bool SceneHasReflections()
{
	return true;
}

// Fog is applied after the material.
void applyUnityFog(inout vec3 col, float fogCoord, float ignoreFog)
{
}

float poiSampleCurve4(vec4 curve, float value)
{
	vec3 weights = clamp(value * 3.0 - vec3(0.0, 1.0, 2.0), 0.0, 1.0);
	return clamp(curve.x + dot(weights, curve.yzw - curve.xyz), 0.0, 1.0);
}

vec2 vertexUV(in VertexOut o, int index)
{
	float i = float(index);
	float w0 = clamp(1.0 - abs(i - 0.0), 0.0, 1.0);
	float w1 = clamp(1.0 - abs(i - 1.0), 0.0, 1.0);
	float w2 = clamp(1.0 - abs(i - 2.0), 0.0, 1.0);
	float w3 = clamp(1.0 - abs(i - 3.0), 0.0, 1.0);
	return o.uv[0].xy * w0 + o.uv[0].zw * w1 + o.uv[1].xy * w2 + o.uv[1].zw * w3;
}

vec2 vertexUV(in VertexOut o, float index)
{
	return vertexUV(o, int(index));
}

vec2 vertexUV(in appdata v, int index)
{
	float i = float(index);
	float w0 = clamp(1.0 - abs(i - 0.0), 0.0, 1.0);
	float w1 = clamp(1.0 - abs(i - 1.0), 0.0, 1.0);
	float w2 = clamp(1.0 - abs(i - 2.0), 0.0, 1.0);
	float w3 = clamp(1.0 - abs(i - 3.0), 0.0, 1.0);
	return v.uv0.xy * w0 + v.uv1.xy * w1 + v.uv2.xy * w2 + v.uv3.xy * w3;
}

vec2 vertexUV(in appdata v, float index)
{
	return vertexUV(v, int(index));
}

// Saturation for theme colors. Below 0 this is a plain OKLab chroma reduction toward grey. From 0 to 1
// it strips the achromatic content out of the color, which is what "more saturated" conventionally
// means. Past 1 there is no chroma left for sRGB to give at this hue, so the remainder deepens the color
// by dropping its level instead of the slider going dead.
vec3 poiThemeSaturation(vec3 color, float amount)
{
	if (amount <= 0.0)
	{
		return PoiApplyChromatize(color, amount);
	}
	float minRgb = min(color.r, min(color.g, color.b));
	float maxRgb = max(color.r, max(color.g, color.b));
	float rangeRgb = maxRgb - minRgb;
	// A greyscale color has no hue to push toward, so leave it alone.
	vec3 pureHue = (rangeRgb > 0.0001) ? ((color - minRgb) / rangeRgb) * maxRgb : color;
	vec3 saturated = mix(color, pureHue, clamp(amount, 0.0, 1.0));
	return saturated / (1.0 + 0.35 * max(amount - 1.0, 0.0));
}

// One global theme; the source writes this out four times.
vec4 poiCalculateGlobalTheme(vec4 themeColorHDR, float hue, float hueSpeed, float saturationMethod, float saturation, float value, float brightness, float hueShiftColorSpace)
{
	// Theme colors are defined as HDR; convert to SDR and do the HSV adjustment, then re-apply exposure
	vec3 themeColor = vec3(0.0);
	float themeColorExposure = 0.0;
	DecomposeHDRColor(themeColorHDR.rgb, themeColor, themeColorExposure);

	float themeHue = fract(hue + hueSpeed * POI_TIME.x);
	int method = int(saturationMethod);
	if (method == 0)
	{
		// Saturation/Value (old)
		themeColor = ModifyViaHSV(themeColor, 0.0, saturation, value);
		themeColor = hueShift(themeColor, themeHue, hueShiftColorSpace, 1.0);
	}
	else if (method == 1)
	{
		// Saturation/Brightness (new)
		themeColor = hueShift(themeColor, themeHue, hueShiftColorSpace, 1.0);
		// Hue shifting in OKLab can land outside the sRGB gamut; clamp first so a negative channel never
		// reaches linear_srgb_to_oklab (pow of a negative is NaN).
		themeColor = max(vec3(0.0), themeColor);
		themeColor = poiThemeSaturation(themeColor, saturation);
		// Brightness rides on the exposure rather than the color: the peak rescale below would undo a
		// gain on the color, so the slider could darken but never brighten.
		themeColorExposure += log2(max(brightness + 1.0, 0.0001));
		themeColor = max(vec3(0.0), themeColor);
	}
	else
	{
		themeColor = vec3(1.0, 0.0, 0.0);
	}
	// DecomposeHDRColor guarantees max(channel) <= 1; hue shift, saturation and brightness can each break
	// that. Rescale by the peak instead of clamping per channel, so the hue survives and the authored HDR
	// intensity stays the only source of overbrightness.
	themeColor = max(vec3(0.0), themeColor);
	themeColor /= max(1.0, max(themeColor.r, max(themeColor.g, themeColor.b)));
	return vec4(ApplyHDRExposure(themeColor, themeColorExposure), themeColorHDR.a);
}

// A theme is worked out where a color picks it, from uniforms alone, so a material whose colors pick none
// pays nothing for the four. -xlinka
vec3 poiThemeColor(in PoiMods poiMods, in vec3 srcColor, in float themeIndex)
{
	float poiThemeIdx = clamp(max(themeIndex - 1.0, 0.0), 0.0, 11.0);
	if (themeIndex == 0.0 || poiThemeIdx > 3.0)
	{
		return srcColor;
	}
	switch (int(poiThemeIdx))
	{
		case 0:
			return poiCalculateGlobalTheme(_GlobalThemeColor0, _GlobalThemeHue0, _GlobalThemeHueSpeed0, _GlobalThemeSaturationMethod0, _GlobalThemeSaturation0, _GlobalThemeValue0, _GlobalThemeBrightness0, _GlobalThemeHueShiftColorSpace0).rgb;
		case 1:
			return poiCalculateGlobalTheme(_GlobalThemeColor1, _GlobalThemeHue1, _GlobalThemeHueSpeed1, _GlobalThemeSaturationMethod1, _GlobalThemeSaturation1, _GlobalThemeValue1, _GlobalThemeBrightness1, _GlobalThemeHueShiftColorSpace1).rgb;
		case 2:
			return poiCalculateGlobalTheme(_GlobalThemeColor2, _GlobalThemeHue2, _GlobalThemeHueSpeed2, _GlobalThemeSaturationMethod2, _GlobalThemeSaturation2, _GlobalThemeValue2, _GlobalThemeBrightness2, _GlobalThemeHueShiftColorSpace2).rgb;
	}
	return poiCalculateGlobalTheme(_GlobalThemeColor3, _GlobalThemeHue3, _GlobalThemeHueSpeed3, _GlobalThemeSaturationMethod3, _GlobalThemeSaturation3, _GlobalThemeValue3, _GlobalThemeBrightness3, _GlobalThemeHueShiftColorSpace3).rgb;
}

struct PoiSpriteSheetConfig
{
	float columns;
	float rows;
	float totalFrames;
	float frameSelection;
	float fps;
	float randomStart;
	float inset;
	vec2 textureST;
};

float poiSpriteSheetFrameIndex(PoiSpriteSheetConfig config, float progress, float seed)
{
	float totalFrames = (config.totalFrames > 0.0) ? config.totalFrames : (config.columns * config.rows);
	float frameIndex = 0.0;

	if (config.frameSelection == 0.0)
	frameIndex = floor(seed * totalFrames);
	else if (config.frameSelection == 1.0)
	frameIndex = floor(progress * totalFrames);
	else if (config.frameSelection == 2.0)
	{
		float timeOffset = config.randomStart != 0.0 ? seed * totalFrames : 0.0;
		frameIndex = floor(poiFmod(POI_TIME.y * config.fps + timeOffset, totalFrames));
	}

	return clamp(frameIndex, 0.0, totalFrames - 1.0);
}

float poiSpriteSheetFrameIndex(float columns, float rows, float configTotalFrames, float frameSelection, float fps, float randomStart, float progress, float seed)
{
	float totalFrames = (configTotalFrames > 0.0) ? configTotalFrames : (columns * rows);
	float frameIndex = 0.0;

	if (frameSelection == 0.0)
	frameIndex = floor(seed * totalFrames);
	else if (frameSelection == 1.0)
	frameIndex = floor(progress * totalFrames);
	else if (frameSelection == 2.0)
	{
		float timeOffset = randomStart != 0.0 ? seed * totalFrames : 0.0;
		frameIndex = floor(poiFmod(POI_TIME.y * fps + timeOffset, totalFrames));
	}

	return clamp(frameIndex, 0.0, totalFrames - 1.0);
}

vec2 poiSpriteSheetUV(vec2 uv, float columns, float rows, float frameIndex, float inset, vec2 textureST)
{
	float invCols = 1.0 / columns;
	float invRows = 1.0 / rows;
	vec2 frameSize = vec2(invCols, invRows);

	float column = poiFmod(frameIndex, columns);
	float row = floor(frameIndex * invCols);
	row = rows - 1.0 - row;

	vec2 lookupUV = (uv * frameSize) + vec2(column, row) * frameSize;
	if (inset > 0.0)
	{
		vec2 texelInset = inset * frameSize / max(textureST, vec2(1e-6));
		lookupUV = mix(lookupUV, vec2(column + 1.0, row + 1.0) * frameSize, texelInset);
	}

	return lookupUV;
}

vec2 poiSpriteSheetUV(vec2 uv, PoiSpriteSheetConfig config, float frameIndex)
{
	return poiSpriteSheetUV(uv, config.columns, config.rows, frameIndex, config.inset, config.textureST);
}
