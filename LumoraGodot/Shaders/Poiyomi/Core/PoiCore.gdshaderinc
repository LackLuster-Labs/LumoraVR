// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Poiyomi's per-fragment state, the vertex-to-fragment and fragment-to-light handoffs, and texture
// fetches in Poiyomi's UV space.
//
// UV space: Poiyomi's UVs run with v up from the bottom-left corner, the way its decal positions,
// flipbook rows, panning and text expect. Mesh UVs are turned into that space as they are read
// (poiUvToPoi), and every 2D fetch turns them back (poiTex and friends), so a texture lands where it
// would on the source while asymmetric parameters keep their meaning. Screen positions follow the same
// rule: y up from the bottom of the view.
//
// Stages: Godot's per-object and per-view built-ins exist only inside vertex(), fragment() and light(),
// and varyings may only be written there, so each stage captures what Poiyomi reads into these structs
// with the POI_CAPTURE_* macros, and fragment() hands its structs to light() with POI_HANDOFF_STORE.
// light() lacks the camera position, eye offset and view index; they travel in the handoff. -xlinka

#define POI_TIME vec4(TIME / 20.0, TIME, TIME * 2.0, TIME * 3.0)
#define POI_PAN_UV(uv, pan) ((uv) + POI_TIME.x * (pan))

#define POI_MODE_OPAQUE 0.0
#define POI_MODE_CUTOUT 1.0
#define POI_MODE_FADE 2.0
#define POI_MODE_TRANSPARENT 3.0
#define POI_MODE_ADDITIVE 4.0
#define POI_MODE_SOFTADDITIVE 5.0
#define POI_MODE_MULTIPLICATIVE 6.0
#define POI_MODE_2XMULTIPLICATIVE 7.0
#define POI_MODE_TRANSCLIPPING 9.0

#define POI_DIRECTIONAL 1
#define POI_POINT_SPOT 2

#if !defined(POI_PASS_OUTLINE)
#define POI_PASS_BASE
#endif

// UV sets 4-10 (panosphere, world, polar, distorted, local, view normal, screen) exist where a selector can
// pick one: the generator defines POI_NEEDS_DERIVED_UVS when one of the material's picks 4 or above, and
// distortion writes its own. Elsewhere the array holds the four mesh UVs. -xlinka
#if defined(POI_NEEDS_DERIVED_UVS) || defined(POI_DISTORTION)
#define POI_UV_DERIVED_SETS
#define POI_UV_COUNT 11
#else
#define POI_UV_COUNT 4
#endif

struct PoiMesh
{
	// 0 vertex normal, 1 fragment normal; world space.
	vec3 normals[2];
	vec3 objNormal;
	vec3 tangentSpaceNormal;
	vec3 bentNormal;
	float hasBentNormal;
	vec3 binormal[2];
	vec3 tangent[2];
	vec3 worldPos;
	vec3 localPos;
	vec3 objectPosition;
	float objectScale;
	float isFrontFace;
	vec4 vertexColor;
	// 0-3 UV0-UV3, 4 panosphere, 5 world xz, 6 polar, 7 distorted, 8 local position, 9 matcap,
	// 10 screen space.
	vec2 uv[POI_UV_COUNT];
	vec2 parallaxUV;
	vec2 dx;
	vec2 dy;
	uint isRightHand;
	mat4 modelMatrix;
	mat4 invModelMatrix;
	// -1 when the object's transform mirrors it.
	float worldTransformSign;
};

struct PoiCam
{
	// eyeViewDir is this eye's; viewDir is the centre eye's, for stable shading.
	vec3 eyeViewDir;
	vec3 viewDir;
	vec3 forwardDir;
	vec3 worldPos;
	float distanceToVert;
	vec4 clipPos;
	vec4 screenSpacePosition;
	vec3 reflectionDir;
	vec3 vertexReflectionDir;
	vec3 tangentViewDir;
	vec4 posScreenSpace;
	vec2 posScreenPixels;
	vec2 screenUV;
	float vDotN;
	float vDotNCentered;
	vec4 worldDirection;
	mat4 viewMatrix;
	mat4 invViewMatrix;
	mat4 projectionMatrix;
	mat4 invProjectionMatrix;
	vec3 centerEyePos;
	vec2 screenSize;
	vec4 fragCoord;
	float nearPlane;
	float farPlane;
	int eyeIndex;
	bool isOrthographic;
	bool isMirror;
};

struct PoiMods
{
	vec4 Mask;
	vec2 detailMask;
	vec2 backFaceDetailIntensity;
	float globalEmission;
	float globalMask[16];
};

struct PoiLight
{
	vec3 direction;
	float nDotVCentered;
	float attenDistance;
	float attenShadows;
	float attenStrength;
	float attenShadowsStrength;
	float attenuation;
	float additiveShadow;
	vec3 lightCookie;
	vec3 directColor;
	vec3 indirectColor;
	float occlusion;
	float toonShadowMask;
	float detailShadow;
	vec3 halfDir;
	float lightMap;
	float lightMapNoAttenuation;
	vec3 rampedLightMap;
	float vertexNDotL;
	float nDotL;
	float nDotV;
	float vertexNDotV;
	float nDotH;
	float vertexNDotH;
	float lDotv;
	float lDotH;
	float nDotVSaturated;
	float nDotLSaturated;
	float nDotLNormalized;
	vec3 finalLighting;
	vec3 finalLightAdd;
	float directLuminance;
	float indirectLuminance;
	float finalLuminance;
	float lightingMinLightBrightness;
	float lightingCapEnabled;
	float lightingMonochromatic;
	int lightType;
	int isAdditive;
	bool isDirectional;
	// The engine lights the ambient side as ALBEDO * ambient * AO. fragment() writes
	// ALBEDO = baseColor * ambientTint and light() divides each light's share by it, so the shading mode
	// can tint the indirect term the way the source tints its indirect color.
	vec3 ambientTint;
};

struct PoiShadingParams
{
	vec4 debugColor;
	uint lightIndex;
	uint exitCode;
};

struct PoiFragData
{
	float smoothness;
	float smoothness2;
	float metallic;
	float specularMask;
	float reflectionMask;
	vec3 baseColor;
	vec3 finalColor;
	float alpha;
	vec3 emission;
	float toggleVertexLights;
};

struct MochieBRDFData
{
	float isInitialized;
	float metallic;
	float smoothness;
	float smoothness2;
	float specularMask;
	float reflectionMask;
	float roughness;
	float brdfRoughness;
	float brdfRoughness2;
	float percepRough;
	float percepRough2;
	float omr;
	vec3 specCol;
	vec2 adjustedAnisotropy;
	vec3 pbrNSelect;
	vec3 pbrTSelect;
	vec3 pbrBSelect;
};

// The vertex program's input, read from Godot's built-ins with POI_APPDATA_READ inside vertex().
struct appdata
{
	vec4 vertex;
	vec3 normal;
	vec4 tangent;
	vec4 color;
	vec2 uv0;
	vec2 uv1;
	vec2 uv2;
	vec2 uv3;
	uint vertexId;
};

// The vertex program's output. vertex() ends with POI_VERTEX_OUT_WRITE(o); fragment() starts with
// POI_VERTEX_OUT_READ(i). pos is the fragment position as the source's fragment stage sees it: pixel,
// depth, clip-space w.
struct VertexOut
{
	vec4 pos;
	vec4 uv[2];
	// xyz world normal, w renderer scale.
	vec4 normal;
	vec4 tangent;
	vec4 worldPos;
	vec4 localPos;
	vec4 vertexColor;
	float worldDir;
};

PoiMesh PoiMesh_zero()
{
	PoiMesh r;
	r.normals[0] = vec3(0.0);
	r.normals[1] = vec3(0.0);
	r.objNormal = vec3(0.0);
	r.tangentSpaceNormal = vec3(0.0, 0.0, 1.0);
	r.bentNormal = vec3(0.0);
	r.hasBentNormal = 0.0;
	r.binormal[0] = vec3(0.0);
	r.binormal[1] = vec3(0.0);
	r.tangent[0] = vec3(0.0);
	r.tangent[1] = vec3(0.0);
	r.worldPos = vec3(0.0);
	r.localPos = vec3(0.0);
	r.objectPosition = vec3(0.0);
	r.objectScale = 1.0;
	r.isFrontFace = 1.0;
	r.vertexColor = vec4(1.0);
	for (int k = 0; k < POI_UV_COUNT; k++)
	{
		r.uv[k] = vec2(0.0);
	}
	r.parallaxUV = vec2(0.0);
	r.dx = vec2(0.0);
	r.dy = vec2(0.0);
	r.isRightHand = 1u;
	r.modelMatrix = mat4(1.0);
	r.invModelMatrix = mat4(1.0);
	r.worldTransformSign = 1.0;
	return r;
}

PoiCam PoiCam_zero()
{
	PoiCam r;
	r.eyeViewDir = vec3(0.0, 0.0, 1.0);
	r.viewDir = vec3(0.0, 0.0, 1.0);
	r.forwardDir = vec3(0.0, 0.0, -1.0);
	r.worldPos = vec3(0.0);
	r.distanceToVert = 0.0;
	r.clipPos = vec4(0.0);
	r.screenSpacePosition = vec4(0.0);
	r.reflectionDir = vec3(0.0);
	r.vertexReflectionDir = vec3(0.0);
	r.tangentViewDir = vec3(0.0, 0.0, 1.0);
	r.posScreenSpace = vec4(0.0);
	r.posScreenPixels = vec2(0.0);
	r.screenUV = vec2(0.0);
	r.vDotN = 0.0;
	r.vDotNCentered = 0.0;
	r.worldDirection = vec4(0.0);
	r.viewMatrix = mat4(1.0);
	r.invViewMatrix = mat4(1.0);
	r.projectionMatrix = mat4(1.0);
	r.invProjectionMatrix = mat4(1.0);
	r.centerEyePos = vec3(0.0);
	r.screenSize = vec2(1.0);
	r.fragCoord = vec4(0.0);
	r.nearPlane = 0.05;
	r.farPlane = 4000.0;
	r.eyeIndex = 0;
	r.isOrthographic = false;
	r.isMirror = false;
	return r;
}

PoiMods PoiMods_zero()
{
	PoiMods r;
	r.Mask = vec4(0.0);
	r.detailMask = vec2(0.0);
	r.backFaceDetailIntensity = vec2(0.0);
	r.globalEmission = 1.0;
	for (int k = 0; k < 16; k++)
	{
		r.globalMask[k] = 0.0;
	}
	return r;
}

PoiLight PoiLight_zero()
{
	PoiLight r;
	r.direction = vec3(0.0, 1.0, 0.0);
	r.nDotVCentered = 0.0;
	r.attenDistance = 1.0;
	r.attenShadows = 1.0;
	r.attenStrength = 1.0;
	r.attenShadowsStrength = 1.0;
	r.attenuation = 1.0;
	r.additiveShadow = 1.0;
	r.lightCookie = vec3(1.0);
	r.directColor = vec3(0.0);
	r.indirectColor = vec3(0.0);
	r.occlusion = 1.0;
	r.toonShadowMask = 1.0;
	r.detailShadow = 1.0;
	r.halfDir = vec3(0.0);
	r.lightMap = 0.0;
	r.lightMapNoAttenuation = 0.0;
	r.rampedLightMap = vec3(0.0);
	r.vertexNDotL = 0.0;
	r.nDotL = 0.0;
	r.nDotV = 0.0;
	r.vertexNDotV = 0.0;
	r.nDotH = 0.0;
	r.vertexNDotH = 0.0;
	r.lDotv = 0.0;
	r.lDotH = 0.0;
	r.nDotVSaturated = 0.0;
	r.nDotLSaturated = 0.0;
	r.nDotLNormalized = 0.0;
	r.finalLighting = vec3(0.0);
	r.finalLightAdd = vec3(0.0);
	r.directLuminance = 0.0;
	r.indirectLuminance = 0.0;
	r.finalLuminance = 0.0;
	r.lightingMinLightBrightness = 0.0;
	r.lightingCapEnabled = 0.0;
	r.lightingMonochromatic = 0.0;
	r.lightType = 0;
	r.isAdditive = 0;
	r.isDirectional = false;
	r.ambientTint = vec3(1.0);
	return r;
}

PoiShadingParams PoiShadingParams_zero()
{
	PoiShadingParams r;
	r.debugColor = vec4(0.0);
	r.lightIndex = 0u;
	r.exitCode = 0u;
	return r;
}

PoiFragData PoiFragData_zero()
{
	PoiFragData r;
	r.smoothness = 0.0;
	r.smoothness2 = 0.0;
	r.metallic = 0.0;
	r.specularMask = 0.0;
	r.reflectionMask = 0.0;
	r.baseColor = vec3(0.0);
	r.finalColor = vec3(0.0);
	r.alpha = 1.0;
	r.emission = vec3(0.0);
	r.toggleVertexLights = 0.0;
	return r;
}

MochieBRDFData MochieBRDFData_zero()
{
	MochieBRDFData r;
	r.isInitialized = 0.0;
	r.metallic = 0.0;
	r.smoothness = 0.0;
	r.smoothness2 = 0.0;
	r.specularMask = 0.0;
	r.reflectionMask = 0.0;
	r.roughness = 0.0;
	r.brdfRoughness = 0.0;
	r.brdfRoughness2 = 0.0;
	r.percepRough = 0.0;
	r.percepRough2 = 0.0;
	r.omr = 0.0;
	r.specCol = vec3(0.0);
	r.adjustedAnisotropy = vec2(0.0);
	r.pbrNSelect = vec3(0.0);
	r.pbrTSelect = vec3(0.0);
	r.pbrBSelect = vec3(0.0);
	return r;
}

VertexOut VertexOut_zero()
{
	VertexOut r;
	r.pos = vec4(0.0);
	r.uv[0] = vec4(0.0);
	r.uv[1] = vec4(0.0);
	r.normal = vec4(0.0, 1.0, 0.0, 1.0);
	r.tangent = vec4(1.0, 0.0, 0.0, 1.0);
	r.worldPos = vec4(0.0);
	r.localPos = vec4(0.0);
	r.vertexColor = vec4(1.0);
	r.worldDir = 0.0;
	return r;
}

#define PoiInitStruct(type, name) name = type##_zero();

// Godot mesh UV to Poiyomi UV space, and back for a fetch.
vec2 poiUvToPoi(vec2 uv)
{
	return vec2(uv.x, 1.0 - uv.y);
}

vec2 poiUvToTexture(vec2 uv)
{
	return vec2(uv.x, 1.0 - uv.y);
}

// The fetches are macros: a function taking a sampler may only ever be called with textures of one filter
// and repeat setting, and the material's textures mix them.
#define poiTex(tex, uv) texture(tex, poiUvToTexture(uv))
#define poiTexBias(tex, uv, bias) texture(tex, poiUvToTexture(uv), bias)
#define poiTexLod(tex, uv, lod) textureLod(tex, poiUvToTexture(uv), lod)
#define poiTexGrad(tex, uv, dx, dy) textureGrad(tex, poiUvToTexture(uv), vec2((dx).x, -(dx).y), vec2((dy).x, -(dy).y))

// POI2D_SAMPLER_PAN and its relatives: the source's sampler argument has no counterpart, each texture
// carries its own sampler state.
#define poiTexPan(tex, uv, pan) poiTex(tex, POI_PAN_UV(uv, pan))
#define poiTexPanGrad(tex, uv, pan, dx, dy) poiTexGrad(tex, POI_PAN_UV(uv, pan), dx, dy)

#define poiTexArray(tex, uvw) texture(tex, vec3(poiUvToTexture((uvw).xy), (uvw).z))
#define poiTexArrayLod(tex, uvw, lod) textureLod(tex, vec3(poiUvToTexture((uvw).xy), (uvw).z), lod)
#define poiTexArrayGrad(tex, uvw, dx, dy) textureGrad(tex, vec3(poiUvToTexture((uvw).xy), (uvw).z), vec2((dx).x, -(dx).y), vec2((dy).x, -(dy).y))

// _TexelSize: (1 / width, 1 / height, width, height).
#define poiTexelSize(tex) vec4(1.0 / vec2(textureSize(tex, 0)), vec2(textureSize(tex, 0)))

// Tangent-space normal from a normal map fetch. Z is rebuilt, so two-channel normal maps read the same.
vec3 poiUnpackNormal(vec4 packednormal, float scale)
{
	vec3 n;
	n.xy = (packednormal.xy * 2.0 - 1.0) * scale;
	n.z = sqrt(1.0 - clamp(dot(n.xy, n.xy), 0.0, 1.0));
	return n;
}

// Screen textures. uv is in Poiyomi's screen space (y up).
#if defined(POI_NEEDS_DEPTH_TEXTURE)
uniform sampler2D poi_DepthTexture : hint_depth_texture, filter_nearest, repeat_disable;
#endif
#if defined(POI_NEEDS_SCREEN_TEXTURE)
uniform sampler2D poi_ScreenTexture : hint_screen_texture, filter_linear_mipmap, repeat_disable;
#endif

vec2 poiScreenToTexture(vec2 uv)
{
	return vec2(uv.x, 1.0 - uv.y);
}

// Raw depth (1 at the near plane, 0 at the far plane), 0 when no effect asked for the depth texture.
float poiSampleScreenDepth(vec2 screenUV)
{
#if defined(POI_NEEDS_DEPTH_TEXTURE)
	return textureLod(poi_DepthTexture, poiScreenToTexture(screenUV), 0.0).r;
#else
	return 0.0;
#endif
}

bool poiDepthTextureExists()
{
#if defined(POI_NEEDS_DEPTH_TEXTURE)
	return textureSize(poi_DepthTexture, 0).x > 16;
#else
	return false;
#endif
}

// View-space distance along the view axis for a raw depth sample.
float poiLinearEyeDepth(in PoiCam poiCam, float rawDepth)
{
	vec4 v = poiCam.invProjectionMatrix * vec4(0.0, 0.0, rawDepth, 1.0);
	return -v.z / v.w;
}

vec4 poiSampleScreen(vec2 screenUV, float lod)
{
#if defined(POI_NEEDS_SCREEN_TEXTURE)
	return textureLod(poi_ScreenTexture, poiScreenToTexture(screenUV), lod);
#else
	return vec4(0.0);
#endif
}

// Camera and view, for vertex() and fragment(). The other eye sits mirrored about the centre eye.
// Depth is reversed: 1 at the near plane.
#define POI_CAPTURE_CAMERA(cam) \
	cam.viewMatrix = VIEW_MATRIX; \
	cam.invViewMatrix = INV_VIEW_MATRIX; \
	cam.projectionMatrix = PROJECTION_MATRIX; \
	cam.invProjectionMatrix = INV_PROJECTION_MATRIX; \
	cam.worldPos = CAMERA_POSITION_WORLD; \
	cam.centerEyePos = CAMERA_POSITION_WORLD - mat3(INV_VIEW_MATRIX) * EYE_OFFSET; \
	cam.eyeIndex = int(VIEW_INDEX); \
	cam.screenSize = VIEWPORT_SIZE; \
	cam.forwardDir = normalize(-INV_VIEW_MATRIX[2].xyz); \
	cam.isOrthographic = PROJECTION_MATRIX[3][3] == 1.0; \
	cam.isMirror = PROJECTION_MATRIX[0][2] != 0.0 || PROJECTION_MATRIX[1][2] != 0.0; \
	{ \
		vec4 poi_near_h = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 1.0, 1.0); \
		vec4 poi_far_h = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 0.0, 1.0); \
		cam.nearPlane = -poi_near_h.z / poi_near_h.w; \
		cam.farPlane = abs(poi_far_h.w) > 1e-8 ? -poi_far_h.z / poi_far_h.w : 1e8; \
	}

#define POI_CAPTURE_OBJECT(mesh) \
	mesh.modelMatrix = MODEL_MATRIX; \
	mesh.invModelMatrix = inverse(MODEL_MATRIX); \
	mesh.worldTransformSign = determinant(mat3(MODEL_MATRIX)) < 0.0 ? -1.0 : 1.0; \
	mesh.objectPosition = MODEL_MATRIX[3].xyz;

// vertex(): the source's appdata from Godot's attributes. UV2 and UV3 share CUSTOM1.xy/zw.
// tangent.w is the bitangent's handedness.
#define POI_APPDATA_READ(v) \
	v.vertex = vec4(VERTEX, 1.0); \
	v.normal = NORMAL; \
	v.tangent = vec4(TANGENT, dot(cross(NORMAL, TANGENT), BINORMAL) < 0.0 ? -1.0 : 1.0); \
	v.color = COLOR; \
	v.uv0 = poiUvToPoi(UV); \
	v.uv1 = poiUvToPoi(UV2); \
	v.uv2 = poiUvToPoi(CUSTOM1.xy); \
	v.uv3 = poiUvToPoi(CUSTOM1.zw); \
	v.vertexId = uint(VERTEX_ID);

// Godot spends its own varyings on UV, UV2, COLOR, NORMAL, TANGENT, BINORMAL and the view-space
// position, so the vertex output rides those: vertex() writes them (model space) and fragment() reads the
// world position back from VERTEX. Only what has no built-in home gets a slot here.
varying vec4 poi_vo_uv23;
varying vec4 poi_vo_localPos;
// x worldDir, y renderer scale (normal.w), z worldPos.w, w spare.
varying vec4 poi_vo_misc;

// End of vertex(): the outputs the built-ins do not carry. vertex() writes VERTEX, NORMAL, TANGENT and
// BINORMAL itself (model space), from the program's world-space results.
#define POI_VERTEX_OUT_WRITE(o) \
	UV = poiUvToTexture(o.uv[0].xy); \
	UV2 = poiUvToTexture(o.uv[0].zw); \
	COLOR = o.vertexColor; \
	poi_vo_uv23 = o.uv[1]; \
	poi_vo_localPos = o.localPos; \
	poi_vo_misc = vec4(o.worldDir, o.normal.w, o.worldPos.w, 0.0);

// Start of fragment(), before anything writes NORMAL: the vertex output as the source's fragment program
// receives it. tangent.w is the object-space handedness, as in the source; the mirror sign is applied
// where the source applies it.
#define POI_VERTEX_OUT_READ(i) \
	i = VertexOut_zero(); \
	i.pos = vec4(FRAGCOORD.xy, FRAGCOORD.z, 1.0 / FRAGCOORD.w); \
	i.uv[0] = vec4(poiUvToPoi(UV), poiUvToPoi(UV2)); \
	i.uv[1] = poi_vo_uv23; \
	i.normal = vec4(normalize(mat3(INV_VIEW_MATRIX) * NORMAL), poi_vo_misc.y); \
	i.tangent = vec4(normalize(mat3(INV_VIEW_MATRIX) * TANGENT), (dot(cross(NORMAL, TANGENT), BINORMAL) < 0.0 ? -1.0 : 1.0) * (determinant(mat3(MODEL_MATRIX)) < 0.0 ? -1.0 : 1.0)); \
	i.worldPos = vec4((INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz, poi_vo_misc.z); \
	i.localPos = poi_vo_localPos; \
	i.vertexColor = COLOR; \
	i.worldDir = poi_vo_misc.x;

// fragment() to light(). These count against the same slots as the vertex outputs (Godot leaves a shader
// 17 of its 32), so only values fragment() makes cross here, packed: they are never interpolated, so
// colors and masks travel as half pairs and positions as raw float bits. light() rebuilds the rest from its
// own built-ins (POI_HANDOFF_LOAD) and the light pipeline redoes the cheap derived values (UVs, view
// directions) with the functions fragment() used.
varying flat uvec4 poi_fl_h[8];
#if defined(POI_BENTNORMALMAP)
varying flat uvec2 poi_fl_bent;
#define POI_HANDOFF_STORE_BENT(poiMesh) poi_fl_bent = uvec2(packHalf2x16(poiMesh.bentNormal.xy), packHalf2x16(vec2(poiMesh.bentNormal.z, 0.0)));
#define POI_HANDOFF_LOAD_BENT(poiMesh) poiMesh.bentNormal = vec3(unpackHalf2x16(poi_fl_bent.x), unpackHalf2x16(poi_fl_bent.y).x);
#else
#define POI_HANDOFF_STORE_BENT(poiMesh)
#define POI_HANDOFF_LOAD_BENT(poiMesh)
#endif

uint poiHalf2(float a, float b)
{
	return packHalf2x16(vec2(a, b));
}

// Flags: eye index, then front face, orthographic, mirror, right hand.
uint poiPackHandoffFlags(int eyeIndex, float isFrontFace, bool isOrthographic, bool isMirror, uint isRightHand)
{
	return uint(eyeIndex & 1) | (isFrontFace > 0.5 ? 2u : 0u) | (isOrthographic ? 4u : 0u) | (isMirror ? 8u : 0u) | (isRightHand != 0u ? 16u : 0u);
}

#define POI_HANDOFF_STORE(poiMesh, poiCam, poiMods, poiFragData, poiLight) \
	poi_fl_h[0] = uvec4(poiHalf2(poiFragData.baseColor.r, poiFragData.baseColor.g), poiHalf2(poiFragData.baseColor.b, poiFragData.alpha), poiHalf2(poiFragData.smoothness, poiFragData.smoothness2), poiHalf2(poiFragData.metallic, poiFragData.specularMask)); \
	poi_fl_h[1] = uvec4(poiHalf2(poiLight.occlusion, poiLight.toonShadowMask), poiHalf2(poiLight.detailShadow, poiFragData.reflectionMask), poiHalf2(poiLight.lightingMinLightBrightness, poiLight.lightingCapEnabled), poiHalf2(poiLight.lightingMonochromatic, poiLight.attenStrength)); \
	poi_fl_h[2] = uvec4(poiHalf2(poiLight.ambientTint.r, poiLight.ambientTint.g), poiHalf2(poiLight.ambientTint.b, poiMods.globalEmission), poiHalf2(poiMesh.tangentSpaceNormal.x, poiMesh.tangentSpaceNormal.y), poiHalf2(poiMesh.tangentSpaceNormal.z, poiMesh.hasBentNormal)); \
	poi_fl_h[3] = floatBitsToUint(vec4(poiMesh.dx, poiMesh.dy)); \
	poi_fl_h[4] = uvec4(floatBitsToUint(poiCam.centerEyePos), poiPackHandoffFlags(poiCam.eyeIndex, poiMesh.isFrontFace, poiCam.isOrthographic, poiCam.isMirror, poiMesh.isRightHand)); \
	poi_fl_h[5] = uvec4(poiHalf2(poiMesh.normals[0].x, poiMesh.normals[0].y), poiHalf2(poiMesh.normals[0].z, poiMesh.objectScale), poiHalf2(poiMesh.tangent[0].x, poiMesh.tangent[0].y), poiHalf2(poiMesh.tangent[0].z, dot(cross(poiMesh.normals[0], poiMesh.tangent[0]), poiMesh.binormal[0]) < 0.0 ? -1.0 : 1.0)); \
	poi_fl_h[6] = uvec4(poiHalf2(poiMods.globalMask[0], poiMods.globalMask[1]), poiHalf2(poiMods.globalMask[2], poiMods.globalMask[3]), poiHalf2(poiMods.globalMask[4], poiMods.globalMask[5]), poiHalf2(poiMods.globalMask[6], poiMods.globalMask[7])); \
	poi_fl_h[7] = uvec4(poiHalf2(poiMods.globalMask[8], poiMods.globalMask[9]), poiHalf2(poiMods.globalMask[10], poiMods.globalMask[11]), poiHalf2(poiMods.globalMask[12], poiMods.globalMask[13]), poiHalf2(poiMods.globalMask[14], poiMods.globalMask[15])); \
	POI_HANDOFF_STORE_BENT(poiMesh)

// light(): the structs back. The world position comes from the fragment depth, the fragment normal from
// NORMAL (view space); uv[0..3] are the mesh UVs before the UV settings. uv[4..10], the view directions,
// the fragment normal's tangent frame, the vertex color and the light's own fields are
// left for the light pipeline to fill.
#define POI_HANDOFF_LOAD(poiMesh, poiCam, poiMods, poiFragData, poiLight) \
	poiMesh = PoiMesh_zero(); \
	poiCam = PoiCam_zero(); \
	poiMods = PoiMods_zero(); \
	poiFragData = PoiFragData_zero(); \
	poiLight = PoiLight_zero(); \
	{ \
		vec2 poi_h00 = unpackHalf2x16(poi_fl_h[0].x); vec2 poi_h01 = unpackHalf2x16(poi_fl_h[0].y); vec2 poi_h02 = unpackHalf2x16(poi_fl_h[0].z); vec2 poi_h03 = unpackHalf2x16(poi_fl_h[0].w); \
		vec2 poi_h10 = unpackHalf2x16(poi_fl_h[1].x); vec2 poi_h11 = unpackHalf2x16(poi_fl_h[1].y); vec2 poi_h12 = unpackHalf2x16(poi_fl_h[1].z); vec2 poi_h13 = unpackHalf2x16(poi_fl_h[1].w); \
		vec2 poi_h20 = unpackHalf2x16(poi_fl_h[2].x); vec2 poi_h21 = unpackHalf2x16(poi_fl_h[2].y); vec2 poi_h22 = unpackHalf2x16(poi_fl_h[2].z); vec2 poi_h23 = unpackHalf2x16(poi_fl_h[2].w); \
		vec2 poi_h50 = unpackHalf2x16(poi_fl_h[5].x); vec2 poi_h51 = unpackHalf2x16(poi_fl_h[5].y); vec2 poi_h52 = unpackHalf2x16(poi_fl_h[5].z); vec2 poi_h53 = unpackHalf2x16(poi_fl_h[5].w); \
		vec4 poi_dxdy = uintBitsToFloat(poi_fl_h[3]); \
		uint poi_flags = poi_fl_h[4].w; \
		poiFragData.baseColor = vec3(poi_h00, poi_h01.x); \
		poiFragData.alpha = poi_h01.y; \
		poiFragData.smoothness = poi_h02.x; \
		poiFragData.smoothness2 = poi_h02.y; \
		poiFragData.metallic = poi_h03.x; \
		poiFragData.specularMask = poi_h03.y; \
		poiLight.occlusion = poi_h10.x; \
		poiLight.toonShadowMask = poi_h10.y; \
		poiLight.detailShadow = poi_h11.x; \
		poiFragData.reflectionMask = poi_h11.y; \
		poiLight.lightingMinLightBrightness = poi_h12.x; \
		poiLight.lightingCapEnabled = poi_h12.y; \
		poiLight.lightingMonochromatic = poi_h13.x; \
		poiLight.attenStrength = poi_h13.y; \
		poiLight.ambientTint = vec3(poi_h20, poi_h21.x); \
		poiMods.globalEmission = poi_h21.y; \
		poiMesh.tangentSpaceNormal = vec3(poi_h22, poi_h23.x); \
		poiMesh.hasBentNormal = poi_h23.y; \
		poiMesh.dx = poi_dxdy.xy; \
		poiMesh.dy = poi_dxdy.zw; \
		poiCam.centerEyePos = uintBitsToFloat(poi_fl_h[4].xyz); \
		poiCam.eyeIndex = int(poi_flags & 1u); \
		poiMesh.isFrontFace = (poi_flags & 2u) != 0u ? 1.0 : 0.0; \
		poiCam.isOrthographic = (poi_flags & 4u) != 0u; \
		poiCam.isMirror = (poi_flags & 8u) != 0u; \
		poiMesh.isRightHand = (poi_flags & 16u) != 0u ? 1u : 0u; \
		poiMesh.normals[0] = normalize(vec3(poi_h50, poi_h51.x)); \
		poiMesh.objectScale = poi_h51.y; \
		poiMesh.tangent[0] = normalize(vec3(poi_h52, poi_h53.x)); \
		poiMesh.binormal[0] = cross(poiMesh.normals[0], poiMesh.tangent[0]) * poi_h53.y; \
		for (int poi_k = 0; poi_k < 4; poi_k++) \
		{ \
			vec2 poi_m0 = unpackHalf2x16(poi_fl_h[6][poi_k]); \
			vec2 poi_m1 = unpackHalf2x16(poi_fl_h[7][poi_k]); \
			poiMods.globalMask[poi_k * 2] = poi_m0.x; \
			poiMods.globalMask[poi_k * 2 + 1] = poi_m0.y; \
			poiMods.globalMask[8 + poi_k * 2] = poi_m1.x; \
			poiMods.globalMask[8 + poi_k * 2 + 1] = poi_m1.y; \
		} \
	} \
	POI_HANDOFF_LOAD_BENT(poiMesh) \
	poiMesh.modelMatrix = MODEL_MATRIX; \
	poiMesh.invModelMatrix = inverse(MODEL_MATRIX); \
	poiMesh.worldTransformSign = determinant(mat3(MODEL_MATRIX)) < 0.0 ? -1.0 : 1.0; \
	poiMesh.objectPosition = MODEL_MATRIX[3].xyz; \
	poiMesh.normals[1] = normalize(mat3(INV_VIEW_MATRIX) * NORMAL); \
	poiMesh.objNormal = mat3(transpose(MODEL_MATRIX)) * poiMesh.normals[0]; \
	{ \
		vec4 poi_view_h = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0); \
		poiMesh.worldPos = (INV_VIEW_MATRIX * vec4(poi_view_h.xyz / poi_view_h.w, 1.0)).xyz; \
	} \
	poiMesh.localPos = poi_vo_localPos.xyz; \
	poiMesh.uv[0] = poiUvToPoi(UV); \
	poiMesh.uv[1] = poiUvToPoi(UV2); \
	poiMesh.uv[2] = poi_vo_uv23.xy; \
	poiMesh.uv[3] = poi_vo_uv23.zw; \
	poiCam.viewMatrix = VIEW_MATRIX; \
	poiCam.invViewMatrix = INV_VIEW_MATRIX; \
	poiCam.projectionMatrix = PROJECTION_MATRIX; \
	poiCam.invProjectionMatrix = INV_PROJECTION_MATRIX; \
	poiCam.worldPos = INV_VIEW_MATRIX[3].xyz; \
	poiCam.forwardDir = normalize(-INV_VIEW_MATRIX[2].xyz); \
	poiCam.screenSize = VIEWPORT_SIZE; \
	poiCam.fragCoord = FRAGCOORD; \
	{ \
		vec4 poi_near_h = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 1.0, 1.0); \
		vec4 poi_far_h = INV_PROJECTION_MATRIX * vec4(0.0, 0.0, 0.0, 1.0); \
		poiCam.nearPlane = -poi_near_h.z / poi_near_h.w; \
		poiCam.farPlane = abs(poi_far_h.w) > 1e-8 ? -poi_far_h.z / poi_far_h.w : 1e8; \
	}
