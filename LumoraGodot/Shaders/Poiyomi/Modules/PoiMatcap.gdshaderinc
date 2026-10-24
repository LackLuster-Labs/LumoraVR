// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Matcaps 0-3. fragment() has no light, so applyMatcap blends under a stand-in light
// (poiSurfaceBaseLight). applyMatcapLight runs the same blend under each real light and under the stand-in
// and adds the difference to that light's share: light color mix, light mask, the light UV mode and
// Add To Light then follow every light the way the source's per-light passes do.

#if defined(POI_MATCAP_0) || defined(POI_MATCAP_1) || defined(POI_MATCAP_2) || defined(POI_MATCAP_3)
#if !defined(POI_SURFACE_LIGHT_HELPERS)
#define POI_SURFACE_LIGHT_HELPERS
// False where the source shades in one go with the light it already has: shading off, unlit outline.
bool poiSurfaceLightDeferred()
{
#if !defined(POI_SHADING)
	return false;
#else
#if defined(POI_PASS_OUTLINE)
	if (_OutlineLit < 0.5) return false;
#endif
	return true;
#endif
}

// The light fragment() blends under: untinted, from straight above, reaching nothing.
PoiLight poiSurfaceBaseLight(in PoiLight poiLight)
{
	PoiLight r = poiLight;
	r.direction = vec3(0.0, 1.0, 0.0);
	r.directColor = vec3(1.0);
	r.lightMap = 0.0;
	r.rampedLightMap = vec3(0.0);
	r.finalLightAdd = vec3(0.0);
	return r;
}
#endif
#endif

#if defined(POI_MATCAP_0) || defined(POI_MATCAP_1) || defined(POI_MATCAP_2) || defined(POI_MATCAP_3)
void blendMatcap(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMods poiMods, float add, float lightAdd, float multiply, float replace, float mixed, float screen, vec4 matcapColor, float matcapMask, float emissionStrength, float matcapLightMask, uint globalMaskIndex, float globalMaskBlendType)
{
	if (matcapLightMask != 0.0)
	{
		matcapMask *= mix(1.0, poiMax(poiLight.rampedLightMap * poiLight.lightMap), matcapLightMask);
	}
	if (globalMaskIndex > 0u)
	{
		matcapMask = maskBlend(matcapMask, poiMods.globalMask[int(globalMaskIndex) - 1], globalMaskBlendType);
	}

	float matcapAlpha = matcapColor.a * matcapMask;
	poiFragData.baseColor.rgb = mix(poiFragData.baseColor.rgb, matcapColor.rgb, replace * matcapAlpha * 0.999999);
	poiFragData.baseColor.rgb *= mix(vec3(1.0), matcapColor.rgb, multiply * matcapAlpha);
	poiFragData.baseColor.rgb += matcapColor.rgb * add * matcapAlpha;
	poiFragData.baseColor.rgb = mix(poiFragData.baseColor.rgb, blendScreen(poiFragData.baseColor.rgb, matcapColor.rgb), screen * matcapAlpha);
#if defined(POI_PASS_BASE)
	poiLight.finalLightAdd += matcapColor.rgb * lightAdd * matcapAlpha;
#endif
	poiFragData.baseColor.rgb *= 1.0 + matcapColor.rgb * (mixed * matcapAlpha);
	poiFragData.emission += matcapColor.rgb * emissionStrength * matcapAlpha;
}

// World axes and cross products below are those of the source's left-handed world as it lands here
// (x mirrored), so the UVs match the source's.
void getMatcapUV(inout vec2 matcapUV, out vec2 matcapMaskUV, in vec2 matcapPan, in float matcapUVMode, in float matcapUVToBlend, in vec2 matCapBlendUV, in float matcapRotation, in float matcapBorder, in vec3 normal, in PoiCam poiCam, in PoiLight poiLight, in PoiMesh poiMesh, in float matcapNormalStrength, in float matcapVRParallaxStrength)
{
	vec2 uvPan = matcapPan * POI_TIME.x;
	// 0 = center eye (stable across both eyes), 1 = per eye (lilToon's VR Parallax Strength)
	vec3 matcapViewDir = normalize(mix(poiCam.viewDir, poiCam.eyeViewDir, matcapVRParallaxStrength));
	switch (int(matcapUVMode))
	{
		// Normal / UTS
		case 0:
		{
			vec3 viewNormal = (poiCam.viewMatrix * vec4(normal, 0.0)).rgb;
			vec3 NormalBlend_MatCapUV_Detail = viewNormal.rgb * vec3(-1.0, -1.0, 1.0);
			vec3 NormalBlend_MatCapUV_Base = ((poiCam.viewMatrix * vec4(matcapViewDir, 0.0)).rgb * vec3(-1.0, -1.0, 1.0)) + vec3(0.0, 0.0, 1.0);
			vec3 noSknewViewNormal = NormalBlend_MatCapUV_Base * dot(NormalBlend_MatCapUV_Base, NormalBlend_MatCapUV_Detail) / NormalBlend_MatCapUV_Base.b - NormalBlend_MatCapUV_Detail;

			matcapUV = noSknewViewNormal.rg * matcapBorder + 0.5;
			break;
		}
		// Top Pinch
		case 1:
		{
			vec3 worldViewUp = normalize(vec3(0.0, 1.0, 0.0) - matcapViewDir * dot(matcapViewDir, vec3(0.0, 1.0, 0.0)));
			vec3 worldViewRight = normalize(cross(worldViewUp, matcapViewDir));
			matcapUV = vec2(dot(worldViewRight, normal), dot(worldViewUp, normal)) * matcapBorder + 0.5;
			break;
		}
		// Custom Double Sided
		case 2:
		{
			vec3 reflection = reflect(-matcapViewDir, normal);
			vec2 uv = vec2(dot(reflection, vec3(-1.0, 0.0, 0.0)), dot(reflection, vec3(0.0, 1.0, 0.0)));
			matcapUV = uv * matcapBorder + 0.5;
			break;
		}
		// Gradient
		case 3:
		{
			matcapUV = vec2(1.0 - abs(dot(normal, matcapViewDir)));
			break;
		}
		case 4:
		{
			// A solution to top pinch would be so awesome

			vec3 worldUp = vec3(0.0, 1.0, 0.0);
			vec3 tangent = normalize(cross(worldUp, normal));

			vec3 bitangent = normalize(cross(tangent, normal));
			vec2 projection = vec2(0.0);
			vec3 matcapHalfDir = normalize(poiLight.direction + matcapViewDir);
			projection.x = dot(matcapHalfDir, tangent);
			projection.y = dot(matcapHalfDir, bitangent);
			matcapUV = projection * matcapBorder + 0.5;
			break;
		}
	}
	matcapUV = mix(matcapUV, poiMesh.uv[int(matcapUVToBlend)], matCapBlendUV);
	// don't pan the mask UV
	matcapMaskUV = matcapUV;
	matcapUV += uvPan;
	matcapUV = RotateUV(matcapUV, matcapRotation * PI, vec2(0.5, 0.5), 1.0);

	if (IsInMirror(poiCam) && matcapUVMode != 3.0)
	{
		matcapUV.x = 1.0 - matcapUV.x;
	}
}

// The source's applyMatcap, reading the light it is given.
void poiMatcapApply(inout PoiFragData poiFragData, in PoiCam poiCam, in PoiMesh poiMesh, inout PoiLight poiLight, in PoiMods poiMods)
{
	bool sceneHasReflections = SceneHasReflections();
	bool isOutlinePass = false;
#if defined(POI_PASS_OUTLINE)
	isOutlinePass = true;
#endif
	vec4 matcap = vec4(0.0);
	float matcapMask = 0.0;
	vec4 matcap2 = vec4(0.0);
	float matcap2Mask = 0.0;
	vec4 matcap3 = vec4(0.0);
	float matcap3Mask = 0.0;
	vec4 matcap4 = vec4(0.0);
	float matcap4Mask = 0.0;
	vec2 matcapUV = vec2(0.0);
	vec2 matcapMaskUV = vec2(0.0);
	float matcapIntensity = 0.0;

	// Matcap 1
#if defined(POI_MATCAP_0)
	if (!(_MatcapHideWithReflProbe >= 0.5 && sceneHasReflections) && (!isOutlinePass || _MatcapShowInOutline >= 0.5))
	{
		vec3 normal0 = mix(poiMesh.normals[0], poiMesh.normals[1], _MatcapNormal);
#if defined(POI_MATCAP_0_CUSTOM_NORMAL) && defined(PROP_MATCAP0NORMALMAP)
		normal0 = calculateNormal(poiMesh.normals[int(_MatcapNormal)], poiMesh, _Matcap0NormalMap, _Matcap0NormalMap_ST, _Matcap0NormalMapPan, _Matcap0NormalMapUV, _Matcap0NormalMapScale);
#endif

#if defined(PROP_MATCAP)
		getMatcapUV(matcapUV, matcapMaskUV, _MatcapPan.xy, _MatcapUVMode, _MatcapUVToBlend, _MatCapBlendUV1.xy, _MatcapRotation, _MatcapBorder, normal0, poiCam, poiLight, poiMesh, _MatcapNormal, _MatCapVRParallaxStrength);
#else
		if (_Matcap0CircleMaskEnabled >= 0.5)
		{
			getMatcapUV(matcapUV, matcapMaskUV, _MatcapPan.xy, _MatcapUVMode, _MatcapUVToBlend, _MatCapBlendUV1.xy, _MatcapRotation, _MatcapBorder, normal0, poiCam, poiLight, poiMesh, _MatcapNormal, _MatCapVRParallaxStrength);
		}
#endif

		vec4 matcapMaskSample = vec4(1.0);
#if defined(PROP_MATCAPMASK)
		matcapMaskSample = poiTexPan(_MatcapMask, poiUV(poiMesh.uv[int(_MatcapMaskUV)], _MatcapMask_ST), _MatcapMaskPan);
#endif

#if defined(PROP_MATCAP)
		vec2 matcapSampleUV = poiUV(matcapUV, _Matcap_ST);
		vec4 matcapTexelSize0 = poiTexelSize(_Matcap);
		float mipCount0 = floor(log2(max(matcapTexelSize0.z, matcapTexelSize0.w)));

		float matcapSmoothness = _MatcapSmoothness;

		if (_MatcapMaskSmoothnessApply >= 0.5)
		{
			matcapSmoothness *= matcapMaskSample[int(_MatcapMaskSmoothnessChannel)];
		}
		matcapSmoothness = (1.0 - matcapSmoothness) * mipCount0;
		matcap = poiTexLod(_Matcap, matcapSampleUV, matcapSmoothness) * vec4(poiThemeColor(poiMods, _MatcapColor.rgb, _MatcapColorThemeIndex), _MatcapColor.a);
#else
		matcap = vec4(poiThemeColor(poiMods, _MatcapColor.rgb, _MatcapColorThemeIndex), _MatcapColor.a);
#endif
		matcap.rgb *= mix(vec3(1.0), poiLight.directColor, _MatcapLightColorMix);

		matcapIntensity = _MatcapIntensity;
		matcap.rgb *= matcapIntensity;
#if !defined(POI_GRABPASS)
		matcap.rgb = mix(matcap.rgb, matcap.rgb * poiFragData.baseColor.rgb, _MatcapBaseColorMix);
#endif

		if (_MatcapHueShiftEnabled >= 0.5)
		{
			matcap.rgb = hueShift(matcap.rgb, _MatcapHueShift + POI_TIME.x * _MatcapHueShiftSpeed, _MatcapHueShiftColorSpace, _MatcapHueSelectOrShift);
		}

#if defined(PROP_MATCAPMASK)
		matcapMask = matcapMaskSample[int(_MatcapMaskChannel)];
#else
		matcapMask = 1.0;
#endif

		if (_MatcapMaskInvert >= 0.5)
		{
			matcapMask = 1.0 - matcapMask;
		}

		if (_Matcap0CircleMaskEnabled >= 0.5)
		{
			matcapMask *= 1.0 - poiEdgeNonLinear(distance(vec2(0.5, 0.5), matcapMaskUV), _Matcap0CircleMaskBorder, _Matcap0CircleMaskBlur);
		}
		poiFragData.alpha *= mix(1.0, matcap.a, matcapMask * _MatcapAlphaOverride);

		if (float(_MatcapApplyToAlphaEnabled) >= 0.5)
		{
			float matcapAlphaApplyValue = dot(matcap.rgb, vec3(0.299, 0.587, 0.114)); // Greyscale
			if (_MatcapApplyToAlphaSourceBlend == 1) // Max
			{
				matcapAlphaApplyValue = poiMax(matcap.rgb);
			}
			if (_MatcapApplyToAlphaBlendType == 0) // Add
			{
				poiFragData.alpha += mix(0.0, matcapAlphaApplyValue, _MatcapApplyToAlphaBlending);
				poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
			}
			else if (_MatcapApplyToAlphaBlendType == 1) // Multiply
			{
				poiFragData.alpha *= mix(1.0, matcapAlphaApplyValue, _MatcapApplyToAlphaBlending);
			}
		}

		blendMatcap(poiLight, poiFragData, poiMods, _MatcapAdd, _MatcapAddToLight, _MatcapMultiply, _MatcapReplace, _MatcapMixed, _MatcapScreen, matcap, matcapMask, _MatcapEmissionStrength, _MatcapLightMask, uint(_MatcapMaskGlobalMask), _MatcapMaskGlobalMaskBlendType);
	}
#endif
	// Matcap 2
#if defined(POI_MATCAP_1)
	if (!(_Matcap2HideWithReflProbe >= 0.5 && sceneHasReflections) && (!isOutlinePass || _Matcap2ShowInOutline >= 0.5))
	{
		vec3 normal1 = mix(poiMesh.normals[0], poiMesh.normals[1], _Matcap2Normal);
#if defined(POI_MATCAP_1_CUSTOM_NORMAL) && defined(PROP_MATCAP1NORMALMAP)
		normal1 = calculateNormal(poiMesh.normals[int(_Matcap2Normal)], poiMesh, _Matcap1NormalMap, _Matcap1NormalMap_ST, _Matcap1NormalMapPan, _Matcap1NormalMapUV, _Matcap1NormalMapScale);
#endif

#if defined(PROP_MATCAP2)
		getMatcapUV(matcapUV, matcapMaskUV, _Matcap2Pan.xy, _Matcap2UVMode, _Matcap2UVToBlend, _MatCap2ndBlendUV1.xy, _Matcap2Rotation, _Matcap2Border, normal1, poiCam, poiLight, poiMesh, _Matcap2Normal, _MatCap2ndVRParallaxStrength);
#else
		if (_Matcap2CircleMaskEnabled >= 0.5)
		{
			getMatcapUV(matcapUV, matcapMaskUV, _Matcap2Pan.xy, _Matcap2UVMode, _Matcap2UVToBlend, _MatCap2ndBlendUV1.xy, _Matcap2Rotation, _Matcap2Border, normal1, poiCam, poiLight, poiMesh, _Matcap2Normal, _MatCap2ndVRParallaxStrength);
		}
#endif

		vec4 matcap2MaskSample = vec4(1.0);
#if defined(PROP_MATCAP2MASK)
		matcap2MaskSample = poiTexPan(_Matcap2Mask, poiUV(poiMesh.uv[int(_Matcap2MaskUV)], _Matcap2Mask_ST), _Matcap2MaskPan);
#endif

#if defined(PROP_MATCAP2)
		vec2 matcapSampleUV2 = poiUV(matcapUV, _Matcap2_ST);
		vec4 matcapTexelSize2 = poiTexelSize(_Matcap2);
		float mipCount2 = floor(log2(max(matcapTexelSize2.z, matcapTexelSize2.w)));

		float matcap2Smoothness = _Matcap2Smoothness;

		if (_Matcap2MaskSmoothnessApply >= 0.5)
		{
			matcap2Smoothness *= matcap2MaskSample[int(_Matcap2MaskSmoothnessChannel)];
		}
		matcap2Smoothness = (1.0 - matcap2Smoothness) * mipCount2;
		matcap2 = poiTexLod(_Matcap2, matcapSampleUV2, matcap2Smoothness) * vec4(poiThemeColor(poiMods, _Matcap2Color.rgb, _Matcap2ColorThemeIndex), _Matcap2Color.a);
#else
		matcap2 = vec4(poiThemeColor(poiMods, _Matcap2Color.rgb, _Matcap2ColorThemeIndex), _Matcap2Color.a);
#endif
		matcap2.rgb *= mix(vec3(1.0), poiLight.directColor, _Matcap2LightColorMix);

		matcapIntensity = _Matcap2Intensity;
		matcap2.rgb *= matcapIntensity;
#if !defined(POI_GRABPASS)
		matcap2.rgb = mix(matcap2.rgb, matcap2.rgb * poiFragData.baseColor.rgb, _Matcap2BaseColorMix);
#endif

		if (_Matcap2HueShiftEnabled >= 0.5)
		{
			matcap2.rgb = hueShift(matcap2.rgb, _Matcap2HueShift + POI_TIME.x * _Matcap2HueShiftSpeed, _Matcap2HueShiftColorSpace, _Matcap2HueSelectOrShift);
		}

#if defined(PROP_MATCAP2MASK)
		matcap2Mask = matcap2MaskSample[int(_Matcap2MaskChannel)];
#else
		matcap2Mask = 1.0;
#endif
		if (_Matcap2MaskInvert >= 0.5)
		{
			matcap2Mask = 1.0 - matcap2Mask;
		}

		if (_Matcap2CircleMaskEnabled >= 0.5)
		{
			matcap2Mask *= 1.0 - poiEdgeNonLinear(distance(vec2(0.5, 0.5), matcapMaskUV), _Matcap2CircleMaskBorder, _Matcap2CircleMaskBlur);
		}
		poiFragData.alpha *= mix(1.0, matcap2.a, matcap2Mask * _Matcap2AlphaOverride);

		if (float(_Matcap2ApplyToAlphaEnabled) >= 0.5)
		{
			float matcap2AlphaApplyValue = dot(matcap2.rgb, vec3(0.299, 0.587, 0.114)); // Greyscale
			if (_Matcap2ApplyToAlphaSourceBlend == 1) // Max
			{
				matcap2AlphaApplyValue = poiMax(matcap2.rgb);
			}
			if (_Matcap2ApplyToAlphaBlendType == 0) // Add
			{
				poiFragData.alpha += mix(0.0, matcap2AlphaApplyValue, _Matcap2ApplyToAlphaBlending);
				poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
			}
			else if (_Matcap2ApplyToAlphaBlendType == 1) // Multiply
			{
				poiFragData.alpha *= mix(1.0, matcap2AlphaApplyValue, _Matcap2ApplyToAlphaBlending);
			}
		}

		blendMatcap(poiLight, poiFragData, poiMods, _Matcap2Add, _Matcap2AddToLight, _Matcap2Multiply, _Matcap2Replace, _Matcap2Mixed, _Matcap2Screen, matcap2, matcap2Mask, _Matcap2EmissionStrength, _Matcap2LightMask, uint(_Matcap2MaskGlobalMask), _Matcap2MaskGlobalMaskBlendType);
	}
#endif
	// Matcap 3
#if defined(POI_MATCAP_2)
	if (!(_Matcap3HideWithReflProbe >= 0.5 && sceneHasReflections) && (!isOutlinePass || _Matcap3ShowInOutline >= 0.5))
	{
		vec3 normal2 = mix(poiMesh.normals[0], poiMesh.normals[1], _Matcap3Normal);
#if defined(POI_MATCAP_2_CUSTOM_NORMAL) && defined(PROP_MATCAP2NORMALMAP)
		normal2 = calculateNormal(poiMesh.normals[int(_Matcap3Normal)], poiMesh, _Matcap2NormalMap, _Matcap2NormalMap_ST, _Matcap2NormalMapPan, _Matcap2NormalMapUV, _Matcap2NormalMapScale);
#endif

#if defined(PROP_MATCAP3)
		getMatcapUV(matcapUV, matcapMaskUV, _Matcap3Pan.xy, _Matcap3UVMode, _Matcap3UVToBlend, _MatCap3rdBlendUV1.xy, _Matcap3Rotation, _Matcap3Border, normal2, poiCam, poiLight, poiMesh, _Matcap3Normal, _MatCap3rdVRParallaxStrength);
#else
		if (_Matcap3CircleMaskEnabled >= 0.5)
		{
			getMatcapUV(matcapUV, matcapMaskUV, _Matcap3Pan.xy, _Matcap3UVMode, _Matcap3UVToBlend, _MatCap3rdBlendUV1.xy, _Matcap3Rotation, _Matcap3Border, normal2, poiCam, poiLight, poiMesh, _Matcap3Normal, _MatCap3rdVRParallaxStrength);
		}
#endif

		vec4 matcap3MaskSample = vec4(1.0);
#if defined(PROP_MATCAP3MASK)
		matcap3MaskSample = poiTexPan(_Matcap3Mask, poiUV(poiMesh.uv[int(_Matcap3MaskUV)], _Matcap3Mask_ST), _Matcap3MaskPan);
#endif

#if defined(PROP_MATCAP3)
		vec2 matcapSampleUV3 = poiUV(matcapUV, _Matcap3_ST);
		vec4 matcapTexelSize3 = poiTexelSize(_Matcap3);
		float mipCount3 = floor(log2(max(matcapTexelSize3.z, matcapTexelSize3.w)));

		float matcap3Smoothness = _Matcap3Smoothness;

		if (_Matcap3MaskSmoothnessApply >= 0.5)
		{
			matcap3Smoothness *= matcap3MaskSample[int(_Matcap3MaskSmoothnessChannel)];
		}
		matcap3Smoothness = (1.0 - matcap3Smoothness) * mipCount3;
		matcap3 = poiTexLod(_Matcap3, matcapSampleUV3, matcap3Smoothness) * vec4(poiThemeColor(poiMods, _Matcap3Color.rgb, _Matcap3ColorThemeIndex), _Matcap3Color.a);
#else
		matcap3 = vec4(poiThemeColor(poiMods, _Matcap3Color.rgb, _Matcap3ColorThemeIndex), _Matcap3Color.a);
#endif
		matcap3.rgb *= mix(vec3(1.0), poiLight.directColor, _Matcap3LightColorMix);

		matcapIntensity = _Matcap3Intensity;
		matcap3.rgb *= matcapIntensity;
#if !defined(POI_GRABPASS)
		matcap3.rgb = mix(matcap3.rgb, matcap3.rgb * poiFragData.baseColor.rgb, _Matcap3BaseColorMix);
#endif

		if (_Matcap3HueShiftEnabled >= 0.5)
		{
			matcap3.rgb = hueShift(matcap3.rgb, _Matcap3HueShift + POI_TIME.x * _Matcap3HueShiftSpeed, _Matcap3HueShiftColorSpace, _Matcap3HueSelectOrShift);
		}

#if defined(PROP_MATCAP3MASK)
		matcap3Mask = matcap3MaskSample[int(_Matcap3MaskChannel)];
#else
		matcap3Mask = 1.0;
#endif
		if (_Matcap3MaskInvert >= 0.5)
		{
			matcap3Mask = 1.0 - matcap3Mask;
		}

		if (_Matcap3CircleMaskEnabled >= 0.5)
		{
			matcap3Mask *= 1.0 - poiEdgeNonLinear(distance(vec2(0.5, 0.5), matcapMaskUV), _Matcap3CircleMaskBorder, _Matcap3CircleMaskBlur);
		}
		poiFragData.alpha *= mix(1.0, matcap3.a, matcap3Mask * _Matcap3AlphaOverride);

		if (float(_Matcap3ApplyToAlphaEnabled) >= 0.5)
		{
			float matcap3AlphaApplyValue = dot(matcap3.rgb, vec3(0.299, 0.587, 0.114)); // Greyscale
			if (_Matcap3ApplyToAlphaSourceBlend == 1) // Max
			{
				matcap3AlphaApplyValue = poiMax(matcap3.rgb);
			}
			if (_Matcap3ApplyToAlphaBlendType == 0) // Add
			{
				poiFragData.alpha += mix(0.0, matcap3AlphaApplyValue, _Matcap3ApplyToAlphaBlending);
				poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
			}
			else if (_Matcap3ApplyToAlphaBlendType == 1) // Multiply
			{
				poiFragData.alpha *= mix(1.0, matcap3AlphaApplyValue, _Matcap3ApplyToAlphaBlending);
			}
		}

		blendMatcap(poiLight, poiFragData, poiMods, _Matcap3Add, _Matcap3AddToLight, _Matcap3Multiply, _Matcap3Replace, _Matcap3Mixed, _Matcap3Screen, matcap3, matcap3Mask, _Matcap3EmissionStrength, _Matcap3LightMask, uint(_Matcap3MaskGlobalMask), _Matcap3MaskGlobalMaskBlendType);
	}
#endif
	// Matcap 4
#if defined(POI_MATCAP_3)
	if (!(_Matcap4HideWithReflProbe >= 0.5 && sceneHasReflections) && (!isOutlinePass || _Matcap4ShowInOutline >= 0.5))
	{
		vec3 normal3 = mix(poiMesh.normals[0], poiMesh.normals[1], _Matcap4Normal);
#if defined(POI_MATCAP_3_CUSTOM_NORMAL) && defined(PROP_MATCAP3NORMALMAP)
		normal3 = calculateNormal(poiMesh.normals[int(_Matcap4Normal)], poiMesh, _Matcap3NormalMap, _Matcap3NormalMap_ST, _Matcap3NormalMapPan, _Matcap3NormalMapUV, _Matcap3NormalMapScale);
#endif

#if defined(PROP_MATCAP4)
		getMatcapUV(matcapUV, matcapMaskUV, _Matcap4Pan.xy, _Matcap4UVMode, _Matcap4UVToBlend, _MatCap4thBlendUV1.xy, _Matcap4Rotation, _Matcap4Border, normal3, poiCam, poiLight, poiMesh, _Matcap4Normal, _MatCap4thVRParallaxStrength);
#else
		if (_Matcap4CircleMaskEnabled >= 0.5)
		{
			getMatcapUV(matcapUV, matcapMaskUV, _Matcap4Pan.xy, _Matcap4UVMode, _Matcap4UVToBlend, _MatCap4thBlendUV1.xy, _Matcap4Rotation, _Matcap4Border, normal3, poiCam, poiLight, poiMesh, _Matcap4Normal, _MatCap4thVRParallaxStrength);
		}
#endif

		vec4 matcap4MaskSample = vec4(1.0);
#if defined(PROP_MATCAP4MASK)
		matcap4MaskSample = poiTexPan(_Matcap4Mask, poiUV(poiMesh.uv[int(_Matcap4MaskUV)], _Matcap4Mask_ST), _Matcap4MaskPan);
#endif

#if defined(PROP_MATCAP4)
		vec2 matcapSampleUV4 = poiUV(matcapUV, _Matcap4_ST);
		vec4 matcapTexelSize4 = poiTexelSize(_Matcap4);
		float mipCount4 = floor(log2(max(matcapTexelSize4.z, matcapTexelSize4.w)));

		float matcap4Smoothness = _Matcap4Smoothness;

		if (_Matcap4MaskSmoothnessApply >= 0.5)
		{
			matcap4Smoothness *= matcap4MaskSample[int(_Matcap4MaskSmoothnessChannel)];
		}
		matcap4Smoothness = (1.0 - matcap4Smoothness) * mipCount4;
		matcap4 = poiTexLod(_Matcap4, matcapSampleUV4, matcap4Smoothness) * vec4(poiThemeColor(poiMods, _Matcap4Color.rgb, _Matcap4ColorThemeIndex), _Matcap4Color.a);
#else
		matcap4 = vec4(poiThemeColor(poiMods, _Matcap4Color.rgb, _Matcap4ColorThemeIndex), _Matcap4Color.a);
#endif
		matcap4.rgb *= mix(vec3(1.0), poiLight.directColor, _Matcap4LightColorMix);

		matcapIntensity = _Matcap4Intensity;
		matcap4.rgb *= matcapIntensity;
#if !defined(POI_GRABPASS)
		matcap4.rgb = mix(matcap4.rgb, matcap4.rgb * poiFragData.baseColor.rgb, _Matcap4BaseColorMix);
#endif

		if (_Matcap4HueShiftEnabled >= 0.5)
		{
			matcap4.rgb = hueShift(matcap4.rgb, _Matcap4HueShift + POI_TIME.x * _Matcap4HueShiftSpeed, _Matcap4HueShiftColorSpace, _Matcap4HueSelectOrShift);
		}

#if defined(PROP_MATCAP4MASK)
		matcap4Mask = matcap4MaskSample[int(_Matcap4MaskChannel)];
#else
		matcap4Mask = 1.0;
#endif
		if (_Matcap4MaskInvert >= 0.5)
		{
			matcap4Mask = 1.0 - matcap4Mask;
		}

		if (_Matcap4CircleMaskEnabled >= 0.5)
		{
			matcap4Mask *= 1.0 - poiEdgeNonLinear(distance(vec2(0.5, 0.5), matcapMaskUV), _Matcap4CircleMaskBorder, _Matcap4CircleMaskBlur);
		}
		poiFragData.alpha *= mix(1.0, matcap4.a, matcap4Mask * _Matcap4AlphaOverride);

		if (float(_Matcap4ApplyToAlphaEnabled) >= 0.5)
		{
			float matcap4AlphaApplyValue = dot(matcap4.rgb, vec3(0.299, 0.587, 0.114)); // Greyscale
			if (_Matcap4ApplyToAlphaSourceBlend == 1) // Max
			{
				matcap4AlphaApplyValue = poiMax(matcap4.rgb);
			}
			if (_Matcap4ApplyToAlphaBlendType == 0) // Add
			{
				poiFragData.alpha += mix(0.0, matcap4AlphaApplyValue, _Matcap4ApplyToAlphaBlending);
				poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
			}
			else if (_Matcap4ApplyToAlphaBlendType == 1) // Multiply
			{
				poiFragData.alpha *= mix(1.0, matcap4AlphaApplyValue, _Matcap4ApplyToAlphaBlending);
			}
		}

		blendMatcap(poiLight, poiFragData, poiMods, _Matcap4Add, _Matcap4AddToLight, _Matcap4Multiply, _Matcap4Replace, _Matcap4Mixed, _Matcap4Screen, matcap4, matcap4Mask, _Matcap4EmissionStrength, _Matcap4LightMask, uint(_Matcap4MaskGlobalMask), _Matcap4MaskGlobalMaskBlendType);
	}
#endif
}

void applyMatcap(inout PoiFragData poiFragData, in PoiCam poiCam, in PoiMesh poiMesh, inout PoiLight poiLight, in PoiMods poiMods)
{
	bool deferred = poiSurfaceLightDeferred();
	PoiLight matcapLight = poiLight;
	if (deferred)
	{
		matcapLight = poiSurfaceBaseLight(poiLight);
	}
	poiMatcapApply(poiFragData, poiCam, poiMesh, matcapLight, poiMods);
	if (!deferred)
	{
		poiLight.finalLightAdd = matcapLight.finalLightAdd;
	}
}

// Per light: the blend's difference to the stand-in light lands in this light's share, and Add To Light
// plus the emission difference ride the main-light path only (directional lights), as the source's add
// pass leaves them out.
#if defined(POI_MATCAP_READS_LIGHT)
#define POI_HAS_LIGHT_HOOK_MATCAP
#endif
void applyMatcapLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	if (!poiSurfaceLightDeferred())
	{
		return;
	}
	bool lightShaped = false;
	bool lightAdds = false;
#if defined(POI_MATCAP_0)
	lightShaped = lightShaped || _MatcapLightColorMix != 0.0 || _MatcapLightMask != 0.0 || _MatcapUVMode == 4.0;
	lightAdds = lightAdds || _MatcapAddToLight != 0.0;
#endif
#if defined(POI_MATCAP_1)
	lightShaped = lightShaped || _Matcap2LightColorMix != 0.0 || _Matcap2LightMask != 0.0 || _Matcap2UVMode == 4.0;
	lightAdds = lightAdds || _Matcap2AddToLight != 0.0;
#endif
#if defined(POI_MATCAP_2)
	lightShaped = lightShaped || _Matcap3LightColorMix != 0.0 || _Matcap3LightMask != 0.0 || _Matcap3UVMode == 4.0;
	lightAdds = lightAdds || _Matcap3AddToLight != 0.0;
#endif
#if defined(POI_MATCAP_3)
	lightShaped = lightShaped || _Matcap4LightColorMix != 0.0 || _Matcap4LightMask != 0.0 || _Matcap4UVMode == 4.0;
	lightAdds = lightAdds || _Matcap4AddToLight != 0.0;
#endif
	bool mainLight = poiLight.isAdditive == 0;
	if (!lightShaped && !(lightAdds && mainLight))
	{
		return;
	}
	if (poiLightAddsNoShare(poiLight))
	{
		return;
	}

	PoiFragData litData = poiFragData;
	litData.emission = vec3(0.0);
	PoiLight litLight = poiLight;
	litLight.finalLightAdd = vec3(0.0);
	poiMatcapApply(litData, poiCam, poiMesh, litLight, poiMods);
	if (mainLight)
	{
		poiLight.finalLightAdd += litLight.finalLightAdd;
	}
	if (lightShaped)
	{
		PoiFragData baseData = poiFragData;
		baseData.emission = vec3(0.0);
		PoiLight baseLight = poiSurfaceBaseLight(poiLight);
		poiMatcapApply(baseData, poiCam, poiMesh, baseLight, poiMods);
		poiLight.finalLightAdd += (litData.baseColor - baseData.baseColor) * poiLight.finalLighting;
		if (mainLight)
		{
			poiLight.finalLightAdd += (litData.emission - baseData.emission) * poiMods.globalEmission;
		}
	}
}
#endif
