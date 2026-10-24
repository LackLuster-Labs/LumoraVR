// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Poiyomi's fragment program. fragment() runs the source's frag through to its outputs; light() runs the
// per-light part (light data, shading, light dependent modules and the module light hooks) once for every
// light that reaches the pixel.
//
// fragment() keeps the source's finalColor as two parts: poiFragData.baseColor is the lit part (ALBEDO,
// which the engine lights with its ambient and every light()), poiFragData.finalColor the unlit part
// (EMISSION). Post-lighting multiplies scale both and are tracked in poiPostScale, which light() applies to
// what it adds as SPECULAR_LIGHT, so a light's adds see them too. Without shading (or an unlit outline)
// the source's finalLighting is 1: the lit part goes out as EMISSION and ALBEDO is black. -xlinka

// FRAG's own fragment-to-light values: x, y the post-lighting scale (rgb) and the additive lights' detail
// shadow as halves; z flags (bit 0 set: fragment() left early, light() adds nothing); w the vertex color.
varying flat uvec4 poi_fl_frag;
#if defined(POI_PARALLAX) && defined(PROP_HEIGHTMAP) && !defined(POI_PASS_OUTLINE)
// The parallax UV replaces one UV channel; light() puts it back instead of marching the height map again.
varying flat uvec2 poi_fl_parallax;
#endif
#if defined(POI_PASS_OUTLINE)
// Outline color before the light dependent rim light blend and highlight (rgb), then the highlight color.
varying flat uvec3 poi_fl_outline;
#endif

#define POI_FRAG_FLAG_EXIT 1u

#if defined(POI_PASS_OUTLINE)
// Shared by the outline body and the highlight so both stay in step under Color Adjust
vec3 poiOutlineColorAdjust(vec3 color)
{
	float hue = fract(_OutlineHue + _OutlineHueOffsetSpeed * POI_TIME.x);
	color = hueShift(color, hue, _OutlineHueShiftColorSpace, 1.0);
	if (int(_OutlineSaturationMethod) == 0)
	{
		// Saturation/Value (old)
		color = ModifyViaHSV(color, 0.0, _OutlineSaturation, _OutlineValue);
	}
	else if (int(_OutlineSaturationMethod) == 1)
	{
		// Saturation/Brightness (new)
		color = mix(color, vec3(dot(color, vec3(0.3, 0.59, 0.11))), -_OutlineSaturation);
		color = clamp(color * (_OutlineBrightness + 1.0), 0.0, 1.0);
	}
	return pow(abs(color), vec3(_OutlineGamma));
}

// The source blends toward the main light's color (Rim Light mode) and lerps in the screen space highlight
// here; both need the light, so this keeps the color before them in outlineBase and the highlight color in
// outlineHighlight for applyOutlineColorLight, and applies only the light-free part.
void applyOutlineColor(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods, in PoiCam poiCam, inout vec3 outlineBase, inout vec3 outlineHighlight)
{
	if (_EnableOutlines - 0.01 < 0.0)
	{
		discard;
	}
	float OutlineMask = 1.0;
#if defined(PROP_OUTLINEMASK)
	OutlineMask = poiTexPan(_OutlineMask, poiUV(poiMesh.uv[int(_OutlineMaskUV)], _OutlineMask_ST), _OutlineMaskPan)[int(_OutlineMaskChannel)];
#endif
	if (_OutlineVertexColorMask > 0.0)
	{
		OutlineMask *= mix(1.0, poiMesh.vertexColor[int(_OutlineVertexColorMask) - 1], _OutlineVertexColorMaskStrength);
	}
	if (_OutlineClipAtZeroWidth >= 0.5)
	{
		float lineWidth = _LineWidth;
		if (OutlineMask * lineWidth - 0.001 < 0.0)
		{
			discard;
		}
	}
	vec4 col = vec4(1.0);
#if defined(PROP_OUTLINETEXTURE)
	col = poiTexPan(_OutlineTexture, poiUV(poiMesh.uv[int(_OutlineTextureUV)], _OutlineTexture_ST), _OutlineTexturePan);
#endif
	poiFragData.baseColor = mix(col.rgb, col.rgb * poiFragData.baseColor * mix(vec3(1.0), poiFragData.baseColor, _PoiUTSStyleOutlineBlend), _OutlineTintMix);
	vec4 outlineColor = _LineColor;

	// Untinted outline color, used as the optional tint source for the highlight below
	vec3 outlineHighlightTint = poiFragData.baseColor;

	poiFragData.baseColor *= poiThemeColor(poiMods, outlineColor.rgb, _LineColorThemeIndex);
	col.a *= outlineColor.a;
	if (_OutlineHueShift >= 0.5)
	{
		poiFragData.baseColor = poiOutlineColorAdjust(poiFragData.baseColor);
	}

	outlineBase = poiFragData.baseColor;
	outlineHighlight = poiFragData.baseColor;
	if (int(_OutlineExpansionMode) == 2)
	{
		poiFragData.baseColor *= 1.0 - _OutlineRimLightBlend;
	}

	if (_OutlineHighlightEnabled >= 0.5)
	{
		vec3 highlightColor = _OutlineHighlightColor.rgb;
		highlightColor = poiThemeColor(poiMods, highlightColor, _OutlineHighlightColorThemeIndex);
		if (_OutlineHighlightApplyTex >= 0.5)
		{
			highlightColor *= outlineHighlightTint;
		}
		// Applied here rather than earlier so the highlight sees the same tex * color input the body does
		if (_OutlineHueShift >= 0.5)
		{
			highlightColor = poiOutlineColorAdjust(highlightColor);
		}
		outlineHighlight = highlightColor;
	}

	if (_OutlineOverrideAlpha >= 0.5)
	{
		poiFragData.alpha = col.a;
	}
	else
	{
		poiFragData.alpha *= col.a;
	}

	if (_OutlineAlphaDistanceFade >= 0.5)
	{
		vec3 position = _OutlineAlphaDistanceFadeType != 0.0 ? poiMesh.worldPos : poiMesh.objectPosition;
		poiFragData.alpha *= mix(_OutlineAlphaDistanceFadeMinAlpha, _OutlineAlphaDistanceFadeMaxAlpha, poiFxSmoothstep(_OutlineAlphaDistanceFadeMin, _OutlineAlphaDistanceFadeMax, distance(position, poiCam.worldPos)));
	}

	float emission = _OutlineEmission;
	poiFragData.emission += poiFragData.baseColor * emission;
}

// The outline's light dependent part for one directional light: the Rim Light blend toward the light's color
// and the highlight, added as the difference to what applyOutlineColor left in the base color and emission.
void applyOutlineColorLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods, vec3 outlineBase, vec3 outlineHighlight, bool lit)
{
	bool rimBlend = int(_OutlineExpansionMode) == 2;
	if (!rimBlend && _OutlineHighlightEnabled < 0.5)
	{
		return;
	}
	vec3 fragmentBase = rimBlend ? outlineBase * (1.0 - _OutlineRimLightBlend) : outlineBase;
	vec3 baseColor = outlineBase;
	if (rimBlend)
	{
		baseColor = mix(baseColor, poiLight.directColor, _OutlineRimLightBlend);
	}

	float highlightAmount = 0.0;
	if (_OutlineHighlightEnabled >= 0.5)
	{
		// Screen space N dot L: the outline is a hull with no visible shading of its own,
		// so the highlight is driven by where the light sits around the silhouette.
		vec3 viewNormal = mat3(poiCam.viewMatrix) * poiMesh.normals[0];
		vec3 viewLight = mat3(poiCam.viewMatrix) * poiLight.direction;
		vec2 highlightN = length(viewNormal.xy) > 1e-6 ? normalize(viewNormal.xy) : vec2(0.0, 1.0);
		vec2 highlightL = length(viewLight.xy) > 1e-6 ? normalize(viewLight.xy) : vec2(0.0, 1.0);
		float highlightNDotL = dot(highlightN, highlightL) * 0.5 + 0.5;
		highlightAmount = poiEdgeLinear(highlightNDotL, _OutlineHighlightBorder, _OutlineHighlightBlur) * _OutlineHighlightColor.a;
		if (_OutlineHighlightReceiveShadow >= 0.5)
		{
			highlightAmount *= poiLight.attenShadowsStrength;
		}
		baseColor = mix(baseColor, outlineHighlight, highlightAmount);
	}

	// Stacks on top of the outline emission so the highlight can glow on a non-emissive outline
	float emission = _OutlineEmission + _OutlineHighlightEmission * highlightAmount;
	vec3 emissionDelta = baseColor * emission - fragmentBase * _OutlineEmission;
	vec3 baseDelta = baseColor - fragmentBase;
	poiLight.finalLightAdd += (lit ? baseDelta * poiLight.finalLighting : baseDelta) + emissionDelta * poiMods.globalEmission;
}
#endif

void PoiLightingNoLoopModules(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
#if defined(POI_DECAL_0) || defined(POI_DECAL_1) || defined(POI_DECAL_2) || defined(POI_DECAL_3)
	// Sampled once and handed to every decal; each picks a channel with its Mask Channel property.
	vec4 poiDecalMask = vec4(1.0);
#if defined(PROP_DECALMASK)
	poiDecalMask = poiTexPan(_DecalMask, poiUV(poiMesh.uv[int(_DecalMaskUV)], _DecalMask_ST), _DecalMaskPan);
#endif
#endif

#if defined(POI_DECAL_0)
	applyDecals(poiDecalMask, poiFragData, poiMesh, poiCam, poiMods, poiLight);
#endif
#if defined(POI_DECAL_1)
	applyDecals__1(poiDecalMask, poiFragData, poiMesh, poiCam, poiMods, poiLight);
#endif
#if defined(POI_DECAL_2)
	applyDecals__2(poiDecalMask, poiFragData, poiMesh, poiCam, poiMods, poiLight);
#endif
#if defined(POI_DECAL_3)
	applyDecals__3(poiDecalMask, poiFragData, poiMesh, poiCam, poiMods, poiLight);
#endif

#if defined(POI_MATCAP_0) || defined(POI_MATCAP_1) || defined(POI_MATCAP_2) || defined(POI_MATCAP_3)
	applyMatcap(poiFragData, poiCam, poiMesh, poiLight, poiMods);
#endif
}

// The light hooks of the modules PoiLightingNoLoopModules runs, for light().
void PoiLightingNoLoopModulesLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
#if defined(POI_HAS_LIGHT_HOOK_DECALS)
	applyDecalsLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_MATCAP)
	applyMatcapLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
}

void PoiLightingDependantModules(inout PoiLight poiLight, inout PoiFragData poiFragData, inout MochieBRDFData mochieBRDFData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	// The outline pass has none of these.
#if !defined(POI_PASS_OUTLINE)
#if defined(POI_ANISOTROPICS)
	applyAnisotropics(poiFragData, poiLight, poiCam, poiMesh, poiMods);
#endif
#if defined(POI_SUBSURFACESCATTERING)
	applySubsurfaceScattering(poiCam, poiLight, poiMesh, poiFragData, poiMods);
#endif
#if defined(POI_STYLIZED_SPECULAR) && defined(POI_STYLIZED_SPECULAR_UTS)
	CalculateUTSSpecular(poiFragData, poiCam, poiLight, poiMesh, poiMods);
#endif
#if defined(POI_STYLIZED_SPECULAR) && defined(POI_STYLIZED_SPECULAR_LILTOON)
	lilReflection(poiFragData, poiCam, poiLight, poiMesh, poiMods);
#endif
#if defined(POI_MOCHIE_BRDF)
	MochieBRDFSpecular(poiFragData, mochieBRDFData, poiCam, poiLight, poiMesh, poiMods);
#endif
#endif
}

// The light hooks of the modules frag runs after the lighting point, in the source's order. Emission, rim,
// matcap, cubemap and glitter compile theirs only under POI_<module>_READS_LIGHT, which the generator sets
// when the material's values make the hook read the light. -xlinka
void PoiLightingPostModulesLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
#if defined(POI_HAS_LIGHT_HOOK_CUBEMAP)
	applyCubemapLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_FLIPBOOK)
	applyFlipbookLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_RIM)
	applyRimLightingLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_DEPTH_RIM)
	applyDepthRimLightingLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_GLITTER)
	applyGlitterLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_EMISSION)
	applyEmissionLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_REFLECTIONS)
	applyReflectionsLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_BACKLIGHT)
	applyBacklightLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
}

// Eye and centre-eye view directions and the tangent space view direction, as frag sets them up.
void poiFragViewSetup(inout PoiCam poiCam, inout PoiMesh poiMesh, inout vec3 poiCenterCameraPosition, inout float poiCenterViewDistanceSq, inout float poiCenterViewInvDistance)
{
	poiCam.eyeViewDir = !IsOrthographicCamera(poiCam) ? (getCameraPosition(poiCam, false) - poiMesh.worldPos) : poiCam.invViewMatrix[2].xyz;
	poiCam.eyeViewDir = normalize(poiCam.eyeViewDir);
	poiCenterCameraPosition = getCameraPosition(poiCam, true);
	vec3 poiCenterViewDelta = poiCenterCameraPosition - poiMesh.worldPos;
	poiCenterViewDistanceSq = dot(poiCenterViewDelta, poiCenterViewDelta);
	poiCenterViewInvDistance = inversesqrt(max(poiCenterViewDistanceSq, 1e-12));
	poiCam.viewDir = !IsOrthographicCamera(poiCam) ? poiCenterViewDelta * poiCenterViewInvDistance : normalize(poiCam.invViewMatrix[2].xyz);
	vec3 tanToWorld0 = vec3(poiMesh.tangent[0].x, poiMesh.binormal[0].x, poiMesh.normals[0].x);
	vec3 tanToWorld1 = vec3(poiMesh.tangent[0].y, poiMesh.binormal[0].y, poiMesh.normals[0].y);
	vec3 tanToWorld2 = vec3(poiMesh.tangent[0].z, poiMesh.binormal[0].z, poiMesh.normals[0].z);
	vec3 ase_tanViewDir = tanToWorld0 * poiCam.eyeViewDir.x + tanToWorld1 * poiCam.eyeViewDir.y + tanToWorld2 * poiCam.eyeViewDir.z;
	poiCam.tangentViewDir = normalize(ase_tanViewDir);
	poiMesh.parallaxUV = poiCam.tangentViewDir.xy / max(poiCam.tangentViewDir.z, 0.0001);
}

// 0-3 UV0-UV3
// 4 Panosphere UV
// 5 world pos xz
// 6 Polar UV
// 7 Distorted UV
// 8 local pos, 9 view normal, 10 screen space
void poiFragUVSetup(inout PoiMesh poiMesh, in PoiCam poiCam, vec4 clipPos)
{
#if defined(POI_UV_DERIVED_SETS)
	poiMesh.uv[4] = poiMesh.uv[0];
	poiMesh.uv[5] = poiMesh.uv[0];
	poiMesh.uv[6] = poiMesh.uv[0];
	poiMesh.uv[7] = poiMesh.uv[0];
	poiMesh.uv[8] = poiMesh.uv[0];
#endif

	poiMesh.uv[0] = applyUVSettings(poiMesh.uv[0], _UVSettingsTiling0, _UVSettingsOffset0, _UVSettingsAngle0, _UVSettingsPan0, _UVSettingsRotate0);
	poiMesh.uv[1] = applyUVSettings(poiMesh.uv[1], _UVSettingsTiling1, _UVSettingsOffset1, _UVSettingsAngle1, _UVSettingsPan1, _UVSettingsRotate1);
	poiMesh.uv[2] = applyUVSettings(poiMesh.uv[2], _UVSettingsTiling2, _UVSettingsOffset2, _UVSettingsAngle2, _UVSettingsPan2, _UVSettingsRotate2);
	poiMesh.uv[3] = applyUVSettings(poiMesh.uv[3], _UVSettingsTiling3, _UVSettingsOffset3, _UVSettingsAngle3, _UVSettingsPan3, _UVSettingsRotate3);

	if (_UVSettingsShiftBackfaceUV >= 0.5 && poiMesh.isFrontFace < 0.5)
	{
		poiMesh.uv[0].x += 1.0;
		poiMesh.uv[1].x += 1.0;
		poiMesh.uv[2].x += 1.0;
		poiMesh.uv[3].x += 1.0;
	}

#if defined(POI_NEEDS_DERIVED_UVS)
	poiMesh.uv[4] = calculatePanosphereUV(poiMesh, poiCam, _PanoUseBothEyes, _StereoEnabled);
	poiMesh.uv[5] = calculateWorldUV(poiMesh);
	poiMesh.uv[8] = calculatelocalUV(poiMesh);

	poiMesh.uv[9] = calculateViewNormalUV(poiMesh, poiCam);
	poiMesh.uv[10] = calculateScreenSpaceUV(poiCam, clipPos);

	// Polar is computed last
	poiMesh.uv[6] = calculatePolarCoordinate(poiMesh);
#endif

#if defined(POI_DISTORTION)
	poiMesh.uv[7] = distortedUV(poiMesh);
#endif
}

// frag's camera data block; poiCam.worldPos becomes the centre eye from here on, as in the source.
void poiFragCameraData(inout PoiCam poiCam, in PoiMesh poiMesh, vec3 poiCenterCameraPosition, float poiCenterViewDistanceSq, float poiCenterViewInvDistance, vec4 clipPos, float worldDir)
{
	poiCam.forwardDir = getCameraForward(poiCam);
	poiCam.worldPos = poiCenterCameraPosition;
	poiCam.reflectionDir = reflect(-poiCam.eyeViewDir, poiMesh.normals[1]);
	poiCam.vertexReflectionDir = reflect(-poiCam.eyeViewDir, poiMesh.normals[0]);
	poiCam.clipPos = clipPos;
	poiCam.distanceToVert = poiCenterViewDistanceSq * poiCenterViewInvDistance;
	poiCam.posScreenSpace = poiTransformClipSpacetoScreenSpaceFrag(poiCam, poiCam.clipPos);
	poiCam.screenUV = poiCam.posScreenSpace.xy / poiCam.posScreenSpace.w;
	poiCam.posScreenPixels = calcPixelScreenUVs(poiCam, poiCam.posScreenSpace);
	poiCam.vDotN = abs(dot(poiCam.eyeViewDir, poiMesh.normals[1]));
	poiCam.vDotNCentered = abs(dot(poiCam.viewDir, poiMesh.normals[1]));
	poiCam.worldDirection.xyz = poiMesh.worldPos - poiCam.worldPos;
	poiCam.worldDirection.w = worldDir;
}

// Proximity color factor on finalColor (Override Color and Back Face), and on the emission.
void poiProximityColor(in PoiMesh poiMesh, in PoiCam poiCam, in PoiMods poiMods, inout vec3 colorScale, inout vec3 emissionScale)
{
	vec3 position = _FXProximityColorType != 0.0 ? poiMesh.worldPos : poiMesh.objectPosition;
	float dist = distance(position, poiCam.worldPos);
	float lerpVal = inverseLerp(_FXProximityColorMinDistance, _FXProximityColorMaxDistance, dist);
	lerpVal = poiSampleCurve4(_FXProximityColorCurve, lerpVal);
	float halfW = clamp(_FXProximityColorEdgeWidth * 0.5, 0.0, 1.0);
	bool inEdge = (float(_FXProximityColorSolid) >= 0.5) && (lerpVal > (0.5 - halfW)) && (lerpVal < (0.5 + halfW));

	if (float(_FXProximityColorSolid) >= 0.5)
	{
		lerpVal = step(0.5, lerpVal);
	}
	vec3 proxColor = mix(
		poiThemeColor(poiMods, _FXProximityColorMinColor.rgb, _FXProximityColorMinColorThemeIndex),
		poiThemeColor(poiMods, _FXProximityColorMaxColor.rgb, _FXProximityColorMaxColorThemeIndex),
		lerpVal);

	if (float(_FXProximityColorOverrideColor) >= 0.5)
	{
		colorScale *= proxColor;
		colorScale *= inEdge ? _FXProximityColorEdgeColor.rgb : vec3(1.0);
	}

	if (float(_FXProximityColorOverrideEmission) >= 0.5)
	{
		emissionScale *= proxColor;
		emissionScale *= inEdge ? _FXProximityColorEdgeColor.rgb : vec3(1.0);
	}

	if (float(_FXProximityColorBackFace) >= 0.5)
	{
		colorScale *= mix(_FXProximityColorMinColor.rgb, vec3(1.0), clamp(poiMesh.isFrontFace, 0.0, 1.0));
	}
}

void fragment()
{
	VertexOut i;
	POI_VERTEX_OUT_READ(i)
	// Under cull_front and cull_disabled the engine hands a back face its normal flipped; the source's is the
	// interpolated vertex normal.
	if (!FRONT_FACING)
	{
		i.normal.xyz = -i.normal.xyz;
	}

	PoiMesh poiMesh;
	PoiInitStruct(PoiMesh, poiMesh)

	PoiLight poiLight;
	PoiInitStruct(PoiLight, poiLight)

	PoiCam poiCam;
	PoiInitStruct(PoiCam, poiCam)

	PoiMods poiMods;
	PoiInitStruct(PoiMods, poiMods)

	poiMods.globalEmission = 1.0;

	PoiFragData poiFragData;
	PoiInitStruct(PoiFragData, poiFragData)

	poiFragData.smoothness = 1.0;
	poiFragData.smoothness2 = 1.0;
	poiFragData.metallic = 1.0;
	poiFragData.specularMask = 1.0;
	poiFragData.reflectionMask = 1.0;
	poiFragData.alpha = 1.0;

	MochieBRDFData mochieBRDFData;
	PoiInitStruct(MochieBRDFData, mochieBRDFData)

	POI_CAPTURE_OBJECT(poiMesh)
	POI_CAPTURE_CAMERA(poiCam)
	poiCam.fragCoord = FRAGCOORD;

	vec3 poiPostScale = vec3(1.0);
	uint poiFragFlags = 0u;
	float poiDetailShadowAdditional = 1.0;
	vec3 poiOutlineBase = vec3(0.0);
	vec3 poiOutlineHighlight = vec3(0.0);

#if defined(POI_UDIMDISCARD)
	applyUDIMDiscard(i, FRONT_FACING ? 1u : 0u);
#endif

	if (_NormalCorrect >= 0.5)
	{
		applyNormalCorrect(poiMesh, i);
	}

	// Mesh Data
	poiMesh.objNormal = mat3(poiMesh.invModelMatrix) * i.normal.xyz;
	poiMesh.normals[0] = i.normal.xyz;
	poiMesh.objectScale = i.normal.w;
	poiMesh.worldPos = i.worldPos.xyz;
	poiMesh.vertexColor = i.vertexColor;

	poiMesh.tangent[0] = i.tangent.xyz;
	poiMesh.binormal[0] = cross(i.normal.xyz, i.tangent.xyz) * (i.tangent.w * poiMesh.worldTransformSign);
	poiMesh.isRightHand = i.tangent.w > 0.0 ? 1u : 0u;
	poiMesh.localPos = i.localPos.xyz;

	poiMesh.isFrontFace = FRONT_FACING ? 1.0 : 0.0;

#if !defined(POI_PASS_OUTLINE)
	if (poiMesh.isFrontFace < 0.5 && float(_FlipBackfaceNormals) >= 0.5)
	{
		poiMesh.normals[0] *= -1.0;
		poiMesh.tangent[0] *= -1.0;
		poiMesh.binormal[0] *= -1.0;
	}
#endif

	vec3 poiCenterCameraPosition = vec3(0.0);
	float poiCenterViewDistanceSq = 0.0;
	float poiCenterViewInvDistance = 0.0;
	poiFragViewSetup(poiCam, poiMesh, poiCenterCameraPosition, poiCenterViewDistanceSq, poiCenterViewInvDistance);

	poiMesh.uv[0] = i.uv[0].xy;
	poiMesh.uv[1] = i.uv[0].zw;
	poiMesh.uv[2] = i.uv[1].xy;
	poiMesh.uv[3] = i.uv[1].zw;

	poiMesh.dx = dFdx(poiMesh.uv[0]);
	poiMesh.dy = dFdy(poiMesh.uv[0]);

	poiFragUVSetup(poiMesh, poiCam, i.pos);

	poiMods.globalMask[0] = 1.0;
	poiMods.globalMask[1] = 1.0;
	poiMods.globalMask[2] = 1.0;
	poiMods.globalMask[3] = 1.0;
	poiMods.globalMask[4] = 1.0;
	poiMods.globalMask[5] = 1.0;
	poiMods.globalMask[6] = 1.0;
	poiMods.globalMask[7] = 1.0;
	poiMods.globalMask[8] = 1.0;
	poiMods.globalMask[9] = 1.0;
	poiMods.globalMask[10] = 1.0;
	poiMods.globalMask[11] = 1.0;
	poiMods.globalMask[12] = 1.0;
	poiMods.globalMask[13] = 1.0;
	poiMods.globalMask[14] = 1.0;
	poiMods.globalMask[15] = 1.0;

#if defined(POI_GLOBALMASK_TEXTURES)
	ApplyGlobalMaskTextures(poiMesh, poiMods);
#endif
	ApplyGlobalMaskVertexColors(poiMesh, poiMods);
	ApplyGlobalMaskModifiers(poiMesh, poiMods, poiCam);
	if (_GlobalMaskOptionsEnable >= 0.5)
	{
		ApplyGlobalMaskOptions(poiMods);
	}

#if defined(POI_PARALLAX) && defined(PROP_HEIGHTMAP) && !defined(POI_PASS_OUTLINE)
	applyParallax(poiMesh, poiLight, poiCam, poiMods);
	poi_fl_parallax = floatBitsToUint(poiMesh.uv[int(_ParallaxUV)]);
#endif

	vec2 mainUV = poiUV(poiMesh.uv[int(_MainTexUV)], _MainTex_ST);

	if (_MainPixelMode >= 0.5)
	{
		mainUV = sharpSample(poiTexelSize(_MainTex), mainUV);
	}

	vec4 mainTexture = poiTexPanStochastic(_MainTex, mainUV, _MainTexPan, _MainTexStochastic);
	mainTexture.a = max(mainTexture.a, _MainIgnoreTexAlpha);

#if defined(POI_VIDEO_EFFECTS) && defined(POI_PASS_BASE)
	if (_VideoEffectsEnable >= 0.5)
	{
		applyVideoEffectsMainTex(mainTexture, poiMesh);
	}
#endif

	applyMainNormal(poiMesh);

#if defined(POI_BUMP2NDMAP)
	applyMainNormal2(poiMesh, poiMods);
#endif

#if defined(POI_DETAIL) && defined(POI_PASS_BASE)
	ApplyDetailNormal(poiMods, poiMesh);
#endif

#if defined(POI_RGBMASK) && !defined(POI_PASS_OUTLINE)
	calculateRGBNormals(poiMesh, poiMods);
#endif

	poiMesh.normals[0] = normalize(poiMesh.normals[0]);

	poiMesh.normals[1] = normalize(
		poiMesh.tangentSpaceNormal.x * poiMesh.tangent[0] +
		poiMesh.tangentSpaceNormal.y * poiMesh.binormal[0] +
		poiMesh.tangentSpaceNormal.z * poiMesh.normals[0]
	);

	poiMesh.tangent[1] = normalize(cross(poiMesh.binormal[0], -poiMesh.normals[1]));
	poiMesh.binormal[1] = normalize(cross(-poiMesh.normals[1], poiMesh.tangent[0]));

#if defined(POI_BENTNORMALMAP) && defined(PROP_BENTNORMALMAP)
	vec4 bentNormalTexel = poiTexPanStochastic(_BentNormalMap, poiUV(poiMesh.uv[int(_BentNormalMapUV)], _BentNormalMap_ST), _BentNormalMapPan, _BentNormalMapStochastic);
	vec3 bentNormalTS = poiUnpackNormal(bentNormalTexel, 1.0);
	// Convert from tangent space to world space using vertex TBN frame
	// (bent normals are baked relative to geometric normal, not detail normal)
	poiMesh.bentNormal = normalize(
		bentNormalTS.x * poiMesh.tangent[0] +
		bentNormalTS.y * poiMesh.binormal[0] +
		bentNormalTS.z * poiMesh.normals[0]
	);
	poiMesh.hasBentNormal = 1.0;
#endif

#if defined(POI_PASS_OUTLINE)
	poiMesh.normals[1] = poiMesh.normals[0];
#endif

	// Camera data
	poiFragCameraData(poiCam, poiMesh, poiCenterCameraPosition, poiCenterViewDistanceSq, poiCenterViewInvDistance, i.pos, i.worldDir);

#if defined(POI_BLACKLIGHTMASKING)
	calculateBlackLightMasks(poiMesh, poiMods);
#endif

	poiLight.lightingMinLightBrightness = _LightingMinLightBrightness;
	poiLight.lightingCapEnabled = _LightingCapEnabled;
	poiLight.lightingMonochromatic = _LightingMonochromatic;

	// Detail Shadows
	float poiDetailShadowMain = 1.0;
#if defined(PROP_LIGHTINGDETAILSHADOWMAPS)
	vec4 DetailShadows = poiTexPan(_LightingDetailShadowMaps, poiUV(poiMesh.uv[int(_LightingDetailShadowMapsUV)], _LightingDetailShadowMaps_ST), _LightingDetailShadowMapsPan);
	poiDetailShadowAdditional = mix(1.0, DetailShadows.r, _LightingDetailShadowStrengthR) * mix(1.0, DetailShadows.g, _LightingDetailShadowStrengthG) * mix(1.0, DetailShadows.b, _LightingDetailShadowStrengthB) * mix(1.0, DetailShadows.a, _LightingDetailShadowStrengthA);
	poiDetailShadowMain = mix(1.0, DetailShadows.r, _LightingAddDetailShadowStrengthR) * mix(1.0, DetailShadows.g, _LightingAddDetailShadowStrengthG) * mix(1.0, DetailShadows.b, _LightingAddDetailShadowStrengthB) * mix(1.0, DetailShadows.a, _LightingAddDetailShadowStrengthA);
#endif

	if (_LightDataDetailShadowGlobalMaskR > 0.0)
	{
		poiDetailShadowAdditional = maskBlend(poiDetailShadowAdditional, poiMods.globalMask[int(_LightDataDetailShadowGlobalMaskR) - 1], _LightDataDetailShadowGlobalMaskBlendTypeR);
		poiDetailShadowMain = maskBlend(poiDetailShadowMain, poiMods.globalMask[int(_LightDataDetailShadowGlobalMaskR) - 1], _LightDataDetailShadowGlobalMaskBlendTypeR);
	}

	// The main light's here; light() swaps in poiDetailShadowAdditional for omni and spot lights.
	poiLight.detailShadow = poiDetailShadowMain;

	// Shadow Masks
#if defined(PROP_LIGHTINGSHADOWMASKS)
	vec4 ShadowMasks = poiTexPan(_LightingShadowMasks, poiUV(poiMesh.uv[int(_LightingShadowMasksUV)], _LightingShadowMasks_ST), _LightingShadowMasksPan);
	poiLight.toonShadowMask = mix(1.0, ShadowMasks.r, _LightingShadowMaskStrengthR) * mix(1.0, ShadowMasks.g, _LightingShadowMaskStrengthG) * mix(1.0, ShadowMasks.b, _LightingShadowMaskStrengthB) * mix(1.0, ShadowMasks.a, _LightingShadowMaskStrengthA);
#else
	poiLight.toonShadowMask = 1.0;
#endif

	if (_LightDataShadowMaskGlobalMaskR > 0.0)
	{
		poiLight.toonShadowMask = maskBlend(poiLight.toonShadowMask, poiMods.globalMask[int(_LightDataShadowMaskGlobalMaskR) - 1], _LightDataShadowMaskGlobalMaskBlendTypeR);
	}

	// Ambient Occlusion
#if defined(PROP_LIGHTINGAOMAPS)
	vec4 AOMaps = poiTexPan(_LightingAOMaps, poiUV(poiMesh.uv[int(_LightingAOMapsUV)], _LightingAOMaps_ST), _LightingAOMapsPan);
	poiLight.occlusion = min(min(min(mix(1.0, AOMaps.r, _LightDataAOStrengthR), mix(1.0, AOMaps.g, _LightDataAOStrengthG)), mix(1.0, AOMaps.b, _LightDataAOStrengthB)), mix(1.0, AOMaps.a, _LightDataAOStrengthA));
#else
	poiLight.occlusion = 1.0;
#endif

	if (_LightDataAOGlobalMaskR > 0.0)
	{
		poiLight.occlusion = maskBlend(poiLight.occlusion, poiMods.globalMask[int(_LightDataAOGlobalMaskR) - 1], _LightDataAOGlobalMaskBlendTypeR);
	}

	poiLight.lightType = int(POI_DIRECTIONAL);

	PoiShadingParams poiShadingParams;
	PoiInitStruct(PoiShadingParams, poiShadingParams)
	PoiLightDataSetup(poiLight, poiFragData, poiMesh, poiCam, poiMods, poiShadingParams);
	// The source returns here: POI_SAFE_RGB1, or the light data debug color (light() adds each light's).
	bool poiEarlyOut = poiShadingParams.exitCode != 0u || _LightDataDebugEnabled >= 0.5;
	if (poiShadingParams.exitCode != 0u)
	{
		poiFragFlags |= POI_FRAG_FLAG_EXIT;
	}
	bool poiUnlit = false;
	vec4 poiPbrOut = vec4(0.0, 1.0, 0.0, 0.0);
	vec4 poiRadianceOut = vec4(0.0);
	vec2 poiClearcoatOut = vec2(0.0, 0.5);
	if (!poiEarlyOut)
	{
#if defined(POI_MOCHIE_BRDF) && !defined(POI_PASS_OUTLINE)
		MetallicAndSpecularFragDataInit(poiFragData, mochieBRDFData, poiMesh, poiMods);
#endif

		applyMainColor(poiFragData, poiMesh, poiCam, poiMods, mainTexture);

#if defined(POI_VIDEO_EFFECTS) && defined(POI_PASS_BASE)
		if (_VideoEffectsEnable >= 0.5)
		{
			applyVideoEffects(poiFragData, poiCam, poiMesh, poiMods);
		}
#endif

		applyAlphaOptions(poiFragData, poiMesh, poiCam, poiMods);

#if defined(POI_DEPTH_FX)
		applyDepthFX(poiFragData, poiCam, poiMesh, poiMods);
#endif

#if defined(POI_DETAIL) && !defined(POI_PASS_OUTLINE)
		ApplyDetailColor(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_SKIN)
		applySkin(poiFragData, poiMesh, poiMods);
#endif

		applyVertexColor(poiFragData, poiMesh);

#if defined(POI_BACKFACE) && !defined(POI_PASS_OUTLINE)
		ApplyBackFaceColor(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_RGBMASK)
		calculateRGBMask(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_DISSOLVE)
		applyDissolve(poiFragData, poiMesh, poiMods, poiCam, poiLight);
#endif

#if defined(POI_LIGHTING_SHADEMAP) && defined(POI_SHADING) && !defined(POI_PASS_OUTLINE)
		applyShadeMapping(poiFragData, poiMesh, poiLight);
#endif

		// The outline pass runs applyOutlineColor first thing in each PoiLightingNoLoopModules call.
		bool skipShading = false;
#if defined(POI_PASS_OUTLINE)
		if (_OutlineLit < 0.5)
		{
			skipShading = true;
		}
#endif

#if defined(POI_SHADING)
		if (!skipShading)
		{
			// Here PoiCalculateShading shades the ambient state from PoiLightDataSetup into ambientTint; each
			// light's share and PoiLightingDependantModules run in light().
			PoiCalculateShading(poiLight, poiFragData, poiMods, poiMesh, poiCam);
#if defined(POI_PASS_OUTLINE)
			applyOutlineColor(poiFragData, poiMesh, poiLight, poiMods, poiCam, poiOutlineBase, poiOutlineHighlight);
#endif
			PoiLightingNoLoopModules(poiLight, poiFragData, poiMesh, poiCam, poiMods);
		}
		else
		{
			poiLight.finalLighting = vec3(1.0);
			poiLight.rampedLightMap = vec3(1.0);
			poiLight.lightMap = 1.0;
#if defined(POI_PASS_OUTLINE)
			applyOutlineColor(poiFragData, poiMesh, poiLight, poiMods, poiCam, poiOutlineBase, poiOutlineHighlight);
#endif
			PoiLightingNoLoopModules(poiLight, poiFragData, poiMesh, poiCam, poiMods);
		}
#else
		poiLight.finalLighting = vec3(1.0);
		poiLight.rampedLightMap = vec3(poiEdgeNonLinear(poiLight.nDotL, 0.1, 0.1));
		poiLight.lightMap = poiLight.nDotLNormalized;
#if defined(POI_PASS_OUTLINE)
		applyOutlineColor(poiFragData, poiMesh, poiLight, poiMods, poiCam, poiOutlineBase, poiOutlineHighlight);
#endif
		PoiLightingNoLoopModules(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif

		poiLight.directLuminance = dot(poiLight.directColor, vec3(0.299, 0.587, 0.114));
		poiLight.indirectLuminance = dot(poiLight.indirectColor, vec3(0.299, 0.587, 0.114));
		poiLight.finalLuminance = dot(poiLight.finalLighting, vec3(0.299, 0.587, 0.114));

		// Without shading the source runs the no-loop modules a second time here.
#if !defined(POI_SHADING)
		PoiShadingGlobalMasks(poiMods, poiLight);
#if defined(POI_PASS_OUTLINE)
		applyOutlineColor(poiFragData, poiMesh, poiLight, poiMods, poiCam, poiOutlineBase, poiOutlineHighlight);
#endif
		PoiLightingNoLoopModules(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif

		poiUnlit = skipShading;
#if !defined(POI_SHADING)
		poiUnlit = true;
#endif

#if defined(POI_CUBEMAP)
		applyCubemap(poiFragData, poiCam, poiMesh, poiLight, poiMods);
#endif

#if defined(POI_FLIPBOOK_0)
		applyFlipbook(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_FLIPBOOK_1)
		applyFlipbook1(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_RIM_0)
#if defined(POI_RIM_0_POIYOMI)
#if defined(PROP_RIMMASK)
		vec4 rimMaskAndBias = poiTexPan(_RimMask, poiUV(poiMesh.uv[int(_RimMaskUV)], _RimMask_ST), _RimMaskPan);
		float rimMask = rimMaskAndBias[int(_RimMaskChannel)];
		float rimBias = rimMaskAndBias.a;
#else
		float rimMask = 1.0;
		float rimBias = 1.0;
#endif

		if (_RimMaskInvert >= 0.5)
		{
			rimMask = 1.0 - rimMask;
		}

#if defined(PROP_RIMTEX)
		vec4 rimColor = poiTexPan(_RimTex, poiUV(poiMesh.uv[int(_RimTexUV)], _RimTex_ST), _RimTexPan);
#else
		vec4 rimColor = vec4(1.0);
#endif

		ApplyPoiyomiRimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, _Is_NormalMapToRimLight, _RimLightingInvert, _RimPower, _RimLinear, _RimStrength, _RimShadowWidth, _RimShadowToggle, _RimWidth, _RimBlendStrength, rimMask, _RimGlobalMask, _RimGlobalMaskBlendType, rimColor, _RimLightColor, _RimLightColorThemeIndex, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlur, _RimShadowMaskRampType, _RimShadowMaskInvert, _RimShadowMaskStrength, _RimShadowAlpha.xy, _RimApplyGlobalMaskIndex, _RimApplyGlobalMaskBlendType, _RimBaseColorMix, _RimBrightness, _RimPoiBlendMode, rimBias, _RimBiasIntensity, _RimApplyAlpha, _RimApplyAlphaBlend);
#endif
#if defined(POI_RIM_0_UTS2)
#if defined(PROP_SET_RIMLIGHTMASK)
		float Set_RimLightMask_var = poiTexPan(_Set_RimLightMask, poiUV(poiMesh.uv[int(_Set_RimLightMaskUV)], _Set_RimLightMask_ST), _Set_RimLightMaskPan)[int(_Set_RimLightMaskChannel)];
#else
		float Set_RimLightMask_var = 1.0;
#endif
		ApplyUTS2RimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, Set_RimLightMask_var, _RimGlobalMask, _RimGlobalMaskBlendType, _RimLightColor, _RimLightColorThemeIndex, _Is_LightColor_RimLight, _Is_NormalMapToRimLight, _RimLight_Power, _RimLight_InsideMask, _RimLight_FeatherOff, _LightDirection_MaskOn, _Tweak_LightDirection_MaskLevel, _Add_Antipodean_RimLight, _Ap_RimLightColor, _RimApColorThemeIndex, _Is_LightColor_Ap_RimLight, _Ap_RimLight_Power, _Ap_RimLight_FeatherOff, _Tweak_RimLightMaskLevel, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed);
#endif
#endif

#if defined(POI_RIM_1)
#if defined(POI_RIM_1_POIYOMI)
#if defined(PROP_RIM2MASK)
		vec4 rim2MaskAndBias = poiTexPan(_Rim2Mask, poiUV(poiMesh.uv[int(_Rim2MaskUV)], _Rim2Mask_ST), _Rim2MaskPan);
		float rim2Mask = rim2MaskAndBias[int(_Rim2MaskChannel)];
		float rim2Bias = rim2MaskAndBias.a;
#else
		float rim2Mask = 1.0;
		float rim2Bias = 1.0;
#endif

		if (_Rim2MaskInvert >= 0.5)
		{
			rim2Mask = 1.0 - rim2Mask;
		}

#if defined(PROP_RIM2TEX)
		vec4 rim2Color = poiTexPan(_Rim2Tex, poiUV(poiMesh.uv[int(_Rim2TexUV)], _Rim2Tex_ST), _Rim2TexPan);
#else
		vec4 rim2Color = vec4(1.0);
#endif
		ApplyPoiyomiRimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, _Is_NormalMapToRim2Light, _Rim2LightingInvert, _Rim2Power, _Rim2Linear, _Rim2Strength, _Rim2ShadowWidth, _Rim2ShadowToggle, _Rim2Width, _Rim2BlendStrength, rim2Mask, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, rim2Color, _Rim2LightColor, _Rim2LightColorThemeIndex, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2Blur, _Rim2ShadowMaskRampType, _Rim2ShadowMaskInvert, _Rim2ShadowMaskStrength, _Rim2ShadowAlpha.xy, _Rim2ApplyGlobalMaskIndex, _Rim2ApplyGlobalMaskBlendType, _Rim2BaseColorMix, _Rim2Brightness, _RimPoi2BlendMode, rim2Bias, _Rim2BiasIntensity, _Rim2ApplyAlpha, _Rim2ApplyAlphaBlend);
#endif
#if defined(POI_RIM_1_UTS2)
#if defined(PROP_SET_RIM2LIGHTMASK)
		float Set_Rim2LightMask_var = poiTexPan(_Set_Rim2LightMask, poiUV(poiMesh.uv[int(_Set_Rim2LightMaskUV)], _Set_Rim2LightMask_ST), _Set_Rim2LightMaskPan)[int(_Set_Rim2LightMaskChannel)];
#else
		float Set_Rim2LightMask_var = 1.0;
#endif
		ApplyUTS2RimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, Set_Rim2LightMask_var, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2LightColor, _Rim2LightColorThemeIndex, _Is_LightColor_Rim2Light, _Is_NormalMapToRim2Light, _Rim2Light_Power, _Rim2Light_InsideMask, _Rim2Light_FeatherOff, _LightDirection_MaskOn2, _Tweak_LightDirection_MaskLevel2, _Add_Antipodean_Rim2Light, _Ap_Rim2LightColor, _Rim2ApColorThemeIndex, _Is_LightColor_Ap_Rim2Light, _Ap_Rim2Light_Power, _Ap_Rim2Light_FeatherOff, _Tweak_Rim2LightMaskLevel, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed);
#endif
#endif

#if defined(POI_DEPTH_RIM)
		ApplyDepthRimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods);
#endif

#if defined(POI_GLITTER)
		applyGlitter(poiFragData, poiMesh, poiCam, poiLight, poiMods);
#endif

#if defined(POI_PATHING)
		applyPathing(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_MIRROR)
		applyMirror(poiCam, poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_TEXT)
		ApplyTextOverlayColor(poiCam, poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_INTERNALPARALLAX)
		applyInternalParallax(poiFragData, poiMesh, poiCam, poiMods);
#endif

#if defined(POI_VORONOI)
		ApplyVoronoi(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_TRUCHET)
		applyTruchet(poiFragData, poiMesh, poiMods);
#endif

#if defined(POI_EMISSION_0) || defined(POI_EMISSION_1) || defined(POI_EMISSION_2) || defined(POI_EMISSION_3)
		vec3 emissionBaseReplace = vec3(0.0);
#endif
#if defined(POI_EMISSION_0)
		emissionBaseReplace += applyEmission(poiFragData, poiMesh, poiLight, poiCam, poiMods);
#endif
#if defined(POI_EMISSION_1)
		emissionBaseReplace += applyEmission__1(poiFragData, poiMesh, poiLight, poiCam, poiMods);
#endif
#if defined(POI_EMISSION_2)
		emissionBaseReplace += applyEmission__2(poiFragData, poiMesh, poiLight, poiCam, poiMods);
#endif
#if defined(POI_EMISSION_3)
		emissionBaseReplace += applyEmission__3(poiFragData, poiMesh, poiLight, poiCam, poiMods);
#endif
#if defined(POI_EMISSION_0) || defined(POI_EMISSION_1) || defined(POI_EMISSION_2) || defined(POI_EMISSION_3)
		poiFragData.baseColor = mix(poiFragData.baseColor, clamp(emissionBaseReplace, 0.0, 1.0), poiMax(emissionBaseReplace));
#endif

		if (_AlphaPremultiply >= 0.5)
		{
			poiFragData.baseColor *= clamp(poiFragData.alpha, 0.0, 1.0);
		}

		// The source's finalColor = baseColor * finalLighting: the lit part stays in baseColor, the unlit part
		// starts empty.
		poiFragData.finalColor = vec3(0.0);

#if defined(POI_MOCHIE_BRDF)
		MochieBRDFMetallics(poiFragData, mochieBRDFData, poiCam, poiLight, poiMesh, poiMods);
#endif

#if defined(POI_CLEARCOAT)
		poiClearCoat(poiFragData, poiCam, poiLight, poiMesh, poiMods);
#endif

#if defined(POI_ENVIRORIM)
		applyEnvironmentRim(poiFragData, poiCam, poiMesh);
#endif

#if defined(POI_BACKLIGHT)
		ApplyBacklight(poiFragData, poiMesh, poiLight, poiCam, poiMods);
#endif

#if defined(POI_RIM_0) && defined(POI_RIM_0_LILTOON)
#if defined(PROP_RIMCOLORTEX)
		vec4 rimColorTex = poiTexPan(_RimColorTex, poiUV(poiMesh.uv[int(_RimColorTexUV)], _RimColorTex_ST), _RimColorTexPan);
#else
		vec4 rimColorTex = vec4(1.0);
#endif
		ApplyLiltoonRimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, _RimColor, _RimIndirColor, rimColorTex, _RimMainStrength, _RimNormalStrength, _RimDirRange, _RimIndirRange, _RimFresnelPower, _RimBackfaceMask, _RimDirStrength, _RimBorder, _RimBlur, _RimIndirBorder, _RimIndirBlur, _RimShadowMask, _RimEnableLighting, _RimVRParallaxStrength, _RimGlobalMask, _RimGlobalMaskBlendType, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlendMode, _RimMaskOnlyMask, _RimLightColorThemeIndex);
#endif
#if defined(POI_RIM_1) && defined(POI_RIM_1_LILTOON)
#if defined(PROP_RIM2COLORTEX)
		vec4 rim2ColorTex = poiTexPan(_Rim2ColorTex, poiUV(poiMesh.uv[int(_Rim2ColorTexUV)], _Rim2ColorTex_ST), _Rim2ColorTexPan);
#else
		vec4 rim2ColorTex = vec4(1.0);
#endif
		ApplyLiltoonRimLighting(poiFragData, poiMesh, poiCam, poiLight, poiMods, _Rim2Color, _Rim2IndirColor, rim2ColorTex, _Rim2MainStrength, _Rim2NormalStrength, _Rim2DirRange, _Rim2IndirRange, _Rim2FresnelPower, _Rim2BackfaceMask, _Rim2DirStrength, _Rim2Border, _Rim2Blur, _Rim2IndirBorder, _Rim2IndirBlur, _Rim2ShadowMask, _Rim2EnableLighting, _Rim2VRParallaxStrength, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2BlendMode, _Rim2MaskOnlyMask, _Rim2LightColorThemeIndex);
#endif

		poiFragData.finalColor += poiLight.finalLightAdd;

		if (_FXProximityColor >= 0.5)
		{
			vec3 proxColorScale = vec3(1.0);
			vec3 proxEmissionScale = vec3(1.0);
			poiProximityColor(poiMesh, poiCam, poiMods, proxColorScale, proxEmissionScale);
			poiFragData.baseColor *= proxColorScale;
			poiFragData.finalColor *= proxColorScale;
			poiPostScale *= proxColorScale;
			poiFragData.emission *= proxEmissionScale;
		}

		poiFragData.alpha = _AlphaForceOpaque != 0.0 ? 1.0 : poiFragData.alpha;

		ApplyAlphaToCoverage(poiFragData, poiMesh);

		applyDithering(poiFragData, poiCam);

		poiFragData.emission = max(poiFragData.emission * _PPEmissionMultiplier, vec3(0.0));
		poiFragData.baseColor = max(poiFragData.baseColor * _PPFinalColorMultiplier, vec3(0.0));
		poiFragData.finalColor = max(poiFragData.finalColor * _PPFinalColorMultiplier, vec3(0.0));
		poiPostScale *= _PPFinalColorMultiplier;

		if (_FXProximityColor >= 0.5 && float(_FXProximityColorOverrideAlpha) >= 0.5)
		{
			vec3 position = _FXProximityColorType != 0.0 ? poiMesh.worldPos : poiMesh.objectPosition;
			float dist = distance(position, poiCam.worldPos);
			float lerpVal = inverseLerp(_FXProximityColorMinDistance, _FXProximityColorMaxDistance, dist);
			lerpVal = poiSampleCurve4(_FXProximityColorCurve, lerpVal);
			if (float(_FXProximityColorSolid) >= 0.5)
			{
				lerpVal = step(0.5, lerpVal);
			}
			poiFragData.alpha *= mix(_FXProximityColorMinColor.a, _FXProximityColorMaxColor.a, lerpVal);
		}

#if defined(POI_POSTPROCESS)
		applyPostProcessing(poiFragData, poiMesh);
#endif

#if defined(POI_PASS_OUTLINE)
		if (_Mode == POI_MODE_OPAQUE)
		{
			poiFragData.alpha = 1.0;
		}
#endif

		// clip(alpha - _Cutoff): compiled for the cutout and blended modes (see notes).
#if defined(POI_ALPHA_CUTOUT) || defined(POI_ALPHA_BLENDED)
		if (poiFragData.alpha - _Cutoff < 0.0)
		{
			discard;
		}
#endif

#if !defined(POI_PASS_OUTLINE)
		if (_Mode == POI_MODE_CUTOUT && _AlphaToCoverage < 0.5)
		{
			poiFragData.alpha = 1.0;
		}
#endif

		poiFragData.finalColor += poiFragData.emission * poiMods.globalEmission;

		// The engine's environment specular inputs: the cloth mode's probe term, then the reflection modules'.
#if defined(POI_SHADING) && defined(POI_LIGHTING_CLOTH)
		PoiClothReflectionOutputs(poiMesh, poiPbrOut);
#endif
#if defined(POI_HAS_REFLECTION_OUTPUTS)
		applyReflectionOutputs(poiFragData, mochieBRDFData, poiMesh, poiCam, poiLight, poiMods, poiPbrOut, poiRadianceOut);
#endif
#if defined(POI_HAS_CLEARCOAT_OUTPUTS)
		applyClearCoatOutputs(poiFragData, poiMesh, poiCam, poiLight, poiMods, poiClearcoatOut);
#endif
	}

	// Outputs
#if defined(POI_HAS_REFLECTION_OUTPUTS)
	RADIANCE = poiRadianceOut;
#endif
#if defined(POI_HAS_CLEARCOAT_OUTPUTS)
	CLEARCOAT = poiClearcoatOut.x;
	CLEARCOAT_ROUGHNESS = poiClearcoatOut.y;
#endif
	METALLIC = poiPbrOut.x;
	ROUGHNESS = poiPbrOut.y;
	SPECULAR = poiPbrOut.z;

	vec3 poiLit = poiFragData.baseColor;
	vec3 poiUnlitColor = poiFragData.finalColor;
#if defined(POI_BLEND_2X)
	// 2x Multiplicative (DstColor, SrcColor) is 2 * src * dst: blend_mul with the color doubled.
	poiLit *= 2.0;
	poiUnlitColor *= 2.0;
	poiPostScale *= 2.0;
#endif
	if (poiEarlyOut)
	{
		ALBEDO = vec3(0.0);
		EMISSION = poiShadingParams.exitCode != 0u ? mainTexture.rgb * 0.0001 : poiShadingParams.debugColor.rgb;
		poiFragData.alpha = 1.0;
	}
	else if (poiUnlit)
	{
		ALBEDO = vec3(0.0);
		EMISSION = poiUnlitColor + poiLit;
	}
	else
	{
		ALBEDO = poiLit * poiLight.ambientTint;
		EMISSION = poiUnlitColor;
	}
	AO = poiLight.occlusion;
	NORMAL = normalize(mat3(VIEW_MATRIX) * poiMesh.normals[1]);
#if defined(POI_ALPHA_OVERRIDE)
	// Additive with a source factor of One: the engine's blend_add weighs the color by ALPHA.
	ALPHA = POI_ALPHA_OVERRIDE;
#elif defined(POI_ALPHA_BLENDED)
	ALPHA = poiFragData.alpha;
#elif defined(POI_FORCE_ALPHA_PASS)
	// Drawn in the alpha pass so the depth texture holds the opaque scene (and, with depth_prepass_alpha, this
	// surface), as the source's depth effects expect.
	ALPHA = 1.0;
#endif
#if defined(POI_ALPHA_TO_COVERAGE)
	ALPHA = poiFragData.alpha;
	ALPHA_SCISSOR_THRESHOLD = _Cutoff;
	ALPHA_ANTIALIASING_EDGE = 0.0;
	ALPHA_TEXTURE_COORDINATE = poiMesh.uv[0] * poiTexelSize(_MainTex).zw;
#endif

	poi_fl_frag = uvec4(poiHalf2(poiPostScale.r, poiPostScale.g), poiHalf2(poiPostScale.b, poiDetailShadowAdditional), poiFragFlags, packUnorm4x8(clamp(poiMesh.vertexColor, 0.0, 1.0)));
#if defined(POI_PASS_OUTLINE)
	poi_fl_outline = uvec3(poiHalf2(poiOutlineBase.r, poiOutlineBase.g), poiHalf2(poiOutlineBase.b, poiOutlineHighlight.r), poiHalf2(poiOutlineHighlight.g, poiOutlineHighlight.b));
#endif
	POI_HANDOFF_STORE(poiMesh, poiCam, poiMods, poiFragData, poiLight)
}

// Poiyomi's per-light path for one light. fragData is poi_fl_frag; parallaxUV and outline carry the parallax UV
// and the outline colors (zero when the pass has none). Adds this light's diffuse share (before ALBEDO) and its
// unlit adds.
void poiLightProgram(inout PoiMesh poiMesh, inout PoiCam poiCam, inout PoiMods poiMods, inout PoiFragData poiFragData, inout PoiLight poiLight, uvec4 fragData, vec2 parallaxUV, uvec3 outline, vec4 clipPos, float worldDir, vec3 lightDirection, vec3 lightColor, float attenuation, bool isDirectional, inout vec3 diffuseLight, inout vec3 specularLight)
{
	if ((fragData.z & POI_FRAG_FLAG_EXIT) != 0u)
	{
		return;
	}
	vec2 poiPostRG = unpackHalf2x16(fragData.x);
	vec2 poiPostB = unpackHalf2x16(fragData.y);
	vec3 poiPostScale = max(vec3(poiPostRG, poiPostB.x), vec3(0.0));
	float poiDetailShadowAdditional = poiPostB.y;

	// COLOR does not reach light(); the vertex color comes over at 8 bits per channel.
	poiMesh.vertexColor = unpackUnorm4x8(fragData.w);

	// The view directions as fragment() had them: poiCam.worldPos is this eye until poiFragCameraData.
	vec3 poiCenterCameraPosition = vec3(0.0);
	float poiCenterViewDistanceSq = 0.0;
	float poiCenterViewInvDistance = 0.0;
	poiFragViewSetup(poiCam, poiMesh, poiCenterCameraPosition, poiCenterViewDistanceSq, poiCenterViewInvDistance);

	poiFragUVSetup(poiMesh, poiCam, clipPos);
#if defined(POI_PARALLAX) && defined(PROP_HEIGHTMAP) && !defined(POI_PASS_OUTLINE)
	poiMesh.uv[int(_ParallaxUV)] = parallaxUV;
#endif

	// The fragment normal's tangent frame, as frag builds it.
	poiMesh.tangent[1] = normalize(cross(poiMesh.binormal[0], -poiMesh.normals[1]));
	poiMesh.binormal[1] = normalize(cross(-poiMesh.normals[1], poiMesh.tangent[0]));

	poiFragCameraData(poiCam, poiMesh, poiCenterCameraPosition, poiCenterViewDistanceSq, poiCenterViewInvDistance, clipPos, worldDir);

	// Directional lights take the source's main light path, omni and spot lights its add pass path.
	poiLight.isDirectional = isDirectional;
	poiLight.isAdditive = isDirectional ? 0 : 1;
	poiLight.lightType = isDirectional ? int(POI_DIRECTIONAL) : int(POI_POINT_SPOT);
	if (!isDirectional)
	{
		poiLight.detailShadow = poiDetailShadowAdditional;
	}

	PoiShadingParams poiShadingParams;
	PoiInitStruct(PoiShadingParams, poiShadingParams)
	PoiLightDataSetupLight(poiLight, poiFragData, poiMesh, poiCam, poiMods, poiShadingParams, lightDirection, lightColor, attenuation, isDirectional);
	if (poiShadingParams.exitCode != 0u)
	{
		return;
	}
	if (_LightDataDebugEnabled >= 0.5)
	{
		specularLight += poiShadingParams.debugColor.rgb;
		return;
	}

	MochieBRDFData mochieBRDFData;
	PoiInitStruct(MochieBRDFData, mochieBRDFData)
#if defined(POI_MOCHIE_BRDF) && !defined(POI_PASS_OUTLINE)
	{
		// fragment()'s init read the material's starting values (all 1); later changes to these do not reach
		// the BRDF data in the source either.
		PoiFragData mochieInitData = poiFragData;
		mochieInitData.smoothness = 1.0;
		mochieInitData.smoothness2 = 1.0;
		mochieInitData.metallic = 1.0;
		mochieInitData.specularMask = 1.0;
		mochieInitData.reflectionMask = 1.0;
		MetallicAndSpecularFragDataInit(mochieInitData, mochieBRDFData, poiMesh, poiMods);
	}
#endif

	bool skipShading = false;
#if defined(POI_PASS_OUTLINE)
	if (_OutlineLit < 0.5)
	{
		skipShading = true;
	}
#endif

#if defined(POI_SHADING)
	if (!skipShading)
	{
		PoiCalculateShading(poiLight, poiFragData, poiMods, poiMesh, poiCam);
		PoiLightingNoLoopModulesLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
		PoiLightingDependantModules(poiLight, poiFragData, mochieBRDFData, poiMesh, poiCam, poiMods);
	}
	else
	{
		poiLight.finalLighting = vec3(1.0);
		poiLight.rampedLightMap = vec3(1.0);
		poiLight.lightMap = 1.0;
		PoiLightingNoLoopModulesLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
	}
#else
	poiLight.finalLighting = vec3(1.0);
	poiLight.rampedLightMap = vec3(poiEdgeNonLinear(poiLight.nDotL, 0.1, 0.1));
	poiLight.lightMap = poiLight.nDotLNormalized;
	PoiLightingNoLoopModulesLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif

	poiLight.directLuminance = dot(poiLight.directColor, vec3(0.299, 0.587, 0.114));
	poiLight.indirectLuminance = dot(poiLight.indirectColor, vec3(0.299, 0.587, 0.114));
	poiLight.finalLuminance = dot(poiLight.finalLighting, vec3(0.299, 0.587, 0.114));

#if !defined(POI_SHADING)
	PoiShadingGlobalMasks(poiMods, poiLight);
	PoiLightingNoLoopModulesLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
	PoiLightingDependantModules(poiLight, poiFragData, mochieBRDFData, poiMesh, poiCam, poiMods);
#endif

	bool poiUnlit = skipShading;
#if !defined(POI_SHADING)
	poiUnlit = true;
#endif

#if defined(POI_HAS_LIGHT_HOOK_MAIN)
	applyMainLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_HAS_LIGHT_HOOK_DISSOLVE)
	applyDissolveLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
	PoiLightingPostModulesLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);

#if defined(POI_PASS_OUTLINE)
	// The outline pass's light is the main light only.
	if (isDirectional)
	{
		vec2 poiOutline0 = unpackHalf2x16(outline.x);
		vec2 poiOutline1 = unpackHalf2x16(outline.y);
		vec2 poiOutline2 = unpackHalf2x16(outline.z);
		applyOutlineColorLight(poiLight, poiFragData, poiMesh, poiCam, poiMods, vec3(poiOutline0, poiOutline1.x), vec3(poiOutline1.y, poiOutline2), !poiUnlit);
	}
#endif

	// Post processing grades what the light adds, after everything else has added to it.
#if defined(POI_HAS_LIGHT_HOOK_EFFECTS)
	applyEffectsLight(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif

	if (!poiUnlit)
	{
		diffuseLight += poiLight.finalLighting / max(poiLight.ambientTint, vec3(1e-4));
	}
	specularLight += poiLight.finalLightAdd * poiPostScale;
}

void light()
{
	PoiMesh poiMesh;
	PoiCam poiCam;
	PoiMods poiMods;
	PoiFragData poiFragData;
	PoiLight poiLight;
	POI_HANDOFF_LOAD(poiMesh, poiCam, poiMods, poiFragData, poiLight)

	vec2 poiParallaxUV = vec2(0.0);
#if defined(POI_PARALLAX) && defined(PROP_HEIGHTMAP) && !defined(POI_PASS_OUTLINE)
	poiParallaxUV = uintBitsToFloat(poi_fl_parallax);
#endif
	uvec3 poiOutlineData = uvec3(0u);
#if defined(POI_PASS_OUTLINE)
	poiOutlineData = poi_fl_outline;
#endif
	vec4 poiClipPos = vec4(FRAGCOORD.xy, FRAGCOORD.z, 1.0 / FRAGCOORD.w);
	vec3 poiDiffuse = vec3(0.0);
	vec3 poiSpecular = vec3(0.0);
	poiLightProgram(poiMesh, poiCam, poiMods, poiFragData, poiLight, poi_fl_frag, poiParallaxUV, poiOutlineData, poiClipPos, poi_vo_misc.x, normalize(mat3(INV_VIEW_MATRIX) * LIGHT), LIGHT_COLOR / PI, ATTENUATION, LIGHT_IS_DIRECTIONAL, poiDiffuse, poiSpecular);

	DIFFUSE_LIGHT += poiDiffuse;
#if defined(POI_SHADING)
	DIFFUSE_LIGHT = PoiLightingCapDiffuse(DIFFUSE_LIGHT, poiLight);
#endif
	SPECULAR_LIGHT += poiSpecular;
}
