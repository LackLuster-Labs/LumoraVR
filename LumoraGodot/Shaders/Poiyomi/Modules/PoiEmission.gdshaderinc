// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Emission, rim lighting and glitter read Poiyomi's main light while they build the base color and the
// emission. fragment() has no light, so their light-dependent terms are evaluated there with
// poiGlowUnlitLight (the light behind the surface and black, i.e. full shadow), and each module's light
// hook evaluates them again per light, with the light and with that same unlit state, and adds the
// difference: the base color change lit by this light, and the emission change for directional lights
// (Poiyomi's add pass drops emission and unlit adds). -xlinka

PoiLight poiGlowUnlitLight(in PoiLight poiLight, in PoiMesh poiMesh, in PoiCam poiCam)
{
	PoiLight unlit = poiLight;
	unlit.direction = -poiMesh.normals[1];
	unlit.directColor = vec3(0.0);
	unlit.attenuation = 0.0;
	unlit.lightMap = 0.0;
	unlit.lightMapNoAttenuation = 0.0;
	unlit.rampedLightMap = vec3(0.0);
	unlit.nDotL = -1.0;
	unlit.nDotLSaturated = 0.0;
	unlit.nDotLNormalized = 0.0;
	unlit.vertexNDotL = dot(poiMesh.normals[0], unlit.direction);
	vec3 halfVector = unlit.direction + poiCam.eyeViewDir;
	unlit.halfDir = dot(halfVector, halfVector) > 1e-8 ? normalize(halfVector) : poiMesh.normals[1];
	unlit.nDotH = dot(poiMesh.normals[1], unlit.halfDir);
	unlit.finalLighting = vec3(0.0);
	unlit.directLuminance = 0.0;
	return unlit;
}

// litDelta: base color change, lit by this light. unlitDelta: emission and unlit add change, main
// (directional) light only. Unlit shading lights nothing per light (finalLighting is 1 there), so only
// the main light's share applies.
void poiGlowAddLightShare(inout PoiLight poiLight, vec3 litDelta, vec3 unlitDelta)
{
	bool unlitShading = false;
#if !defined(POI_SHADING)
	unlitShading = true;
#endif
#if defined(POI_PASS_OUTLINE)
	unlitShading = unlitShading || _OutlineLit < 0.5;
#endif
	if (unlitShading && !poiLight.isDirectional)
	{
		return;
	}
	poiLight.finalLightAdd += litDelta * poiLight.finalLighting;
	if (poiLight.isDirectional)
	{
		poiLight.finalLightAdd += unlitDelta;
	}
}

#if defined(POI_EMISSION_0) || defined(POI_EMISSION_1) || defined(POI_EMISSION_2) || defined(POI_EMISSION_3)
float calculateGlowInTheDark(in float minLight, in float maxLight, in float minEmissionMultiplier, in float maxEmissionMultiplier, in float enabled, in float worldOrMesh, in PoiLight poiLight)
{
	float glowInTheDarkMultiplier = 1.0;
	if (enabled != 0.0)
	{
		float lightValue = worldOrMesh != 0.0 ? CalculateLuminance(poiLight.finalLighting.rgb) : CalculateLuminance(poiLight.directColor.rgb);
		float gitdeAlpha = clamp(inverseLerp(minLight, maxLight, dot(vec3(lightValue), vec3(0.299, 0.587, 0.114))), 0.0, 1.0);
		glowInTheDarkMultiplier = mix(minEmissionMultiplier, maxEmissionMultiplier, gitdeAlpha);
	}
	return glowInTheDarkMultiplier;
}

float calculateScrollingEmission(in vec3 direction, in float velocity, in float interval, in float scrollWidth, float offset, vec3 position)
{
	scrollWidth = max(scrollWidth, 0.0);
	float phase = 0.0;
	phase = dot(position, direction);
	phase -= (POI_TIME.y + offset) * velocity;
	phase /= interval;
	phase -= floor(phase);
	phase = clamp(phase, 0.0, 1.0);
	return (pow(phase, scrollWidth) + pow(1.0 - phase, scrollWidth * 4.0)) * 0.5;
}

float calculateBlinkingEmission(in float blinkMin, in float blinkMax, in float blinkVelocity, in float offset, in float mode, in float duty)
{
	if (mode == 1.0)
	{
		float phase = fract((POI_TIME.y + offset) * blinkVelocity / (2.0 * PI));
		float on = step(phase, duty);
		return mix(blinkMin, blinkMax, on);
	}
	float amplitude = (blinkMax - blinkMin) * 0.5;
	float base = blinkMin + amplitude;
	return sin((POI_TIME.y + offset) * blinkVelocity) * amplitude + base;
}

// The light-dependent end of applyEmission: fluorescence, hue shift (after it, as Poiyomi orders them)
// and glow in the dark. Color and strength apart, so the light hook can keep a color that does not
// depend on the light. -xlinka
vec3 poiEmissionFinishColor(vec3 emissionColor, in PoiLight poiLight, float fluorescence, float hueShiftEnabled, float hueShiftAmount, float hueShiftSpeed, float hueShiftColorSpace, float hueSelectOrShift, float saturation)
{
	vec3 inverseLighting = clamp((1.0 - poiLight.directColor) * sqrt(poiLight.directColor), 0.0, 1.0);
	emissionColor = mix(emissionColor.rgb, emissionColor.rgb * inverseLighting, fluorescence);
	if (hueShiftEnabled >= 0.5)
	{
		emissionColor = hueShift(emissionColor, fract(hueShiftAmount + hueShiftSpeed * POI_TIME.x), hueShiftColorSpace, hueSelectOrShift);
		emissionColor = mix(emissionColor, vec3(dot(emissionColor, vec3(0.3, 0.59, 0.11))), -saturation);
	}
	return emissionColor;
}

vec3 poiEmissionFinishStrength(vec3 emissionColor, float emissionStrength, in PoiLight poiLight, float gitdMinLight, float gitdMaxLight, float gitdMinMultiplier, float gitdMaxMultiplier, float gitdEnabled, float gitdWorldOrMesh)
{
	float glowInTheDarkMultiplier = calculateGlowInTheDark(gitdMinLight, gitdMaxLight, gitdMinMultiplier, gitdMaxMultiplier, gitdEnabled, gitdWorldOrMesh, poiLight);
	emissionStrength *= glowInTheDarkMultiplier;
	return max(emissionStrength * emissionColor, vec3(0.0));
}

vec3 poiEmissionFinish(vec3 emissionColor, float emissionStrength, in PoiLight poiLight, float fluorescence, float hueShiftEnabled, float hueShiftAmount, float hueShiftSpeed, float hueShiftColorSpace, float hueSelectOrShift, float saturation, float gitdMinLight, float gitdMaxLight, float gitdMinMultiplier, float gitdMaxMultiplier, float gitdEnabled, float gitdWorldOrMesh)
{
	vec3 color = poiEmissionFinishColor(emissionColor, poiLight, fluorescence, hueShiftEnabled, hueShiftAmount, hueShiftSpeed, hueShiftColorSpace, hueSelectOrShift, saturation);
	return poiEmissionFinishStrength(color, emissionStrength, poiLight, gitdMinLight, gitdMaxLight, gitdMinMultiplier, gitdMaxMultiplier, gitdEnabled, gitdWorldOrMesh);
}
#endif

#if defined(POI_EMISSION_0)
// applyEmission's light-free part: the color before fluorescence and the strength before glow in the dark.
void poiEmissionInputs(in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods, out vec3 emissionColor0, out float emissionStrength0)
{
	float emissionAlpha = _EmissionColor.a;
	emissionStrength0 = _EmissionStrength;

#if defined(POI_GRABPASS)
	float mixBaseColor = 0.0;
#else
	float mixBaseColor = _EmissionBaseColorAsMap;
#endif

	emissionColor0 = mix(vec3(1.0), poiFragData.baseColor, mixBaseColor).rgb * poiThemeColor(poiMods, _EmissionColor.rgb, _EmissionColorThemeIndex);
#if defined(PROP_EMISSIONMAP)
	vec4 emissionTex = vec4(0.0);
	if (_EmissionCenterOutEnabled < 0.5)
	{
		emissionTex = poiTexPan(_EmissionMap, poiUV(poiMesh.uv[int(_EmissionMapUV)], _EmissionMap_ST), _EmissionMapPan);
	}
	else
	{
		float centerOutValue = poiLight.nDotVCentered;
		centerOutValue = _EmissionCenterOutLinear != 0.0 ? 1.0 - acos(centerOutValue) * 0.63662 : centerOutValue;
		emissionTex = poiTex(_EmissionMap, ((0.5 + centerOutValue * 0.5) * _EmissionMap_ST.xy) + POI_TIME.x * _EmissionCenterOutSpeed);
	}
	emissionColor0 = emissionTex.rgb * mix(vec3(1.0), poiFragData.baseColor, mixBaseColor).rgb * poiThemeColor(poiMods, _EmissionColor.rgb, _EmissionColorThemeIndex);
	emissionAlpha = emissionAlpha * emissionTex.a;
#endif

	emissionStrength0 *= emissionAlpha;

	if (_ScrollingEmission >= 0.5)
	{
		vec3 pos = poiMesh.localPos;
		if (_EmissionScrollingVertexColor >= 0.5)
		{
			pos = poiMesh.vertexColor.rgb;
		}

		if (_EmissionScrollingUseCurve >= 0.5)
		{
#if defined(PROP_EMISSIONSCROLLINGCURVE)
			emissionStrength0 *= poiTex(_EmissionScrollingCurve, poiUV(poiMesh.uv[int(_EmissionMapUV)], _EmissionScrollingCurve_ST) + (dot(pos, _EmissiveScroll_Direction.xyz) * _EmissiveScroll_Interval) + POI_TIME.x * _EmissiveScroll_Velocity).r;
#endif
		}
		else
		{
			emissionStrength0 *= calculateScrollingEmission(_EmissiveScroll_Direction.xyz, _EmissiveScroll_Velocity, _EmissiveScroll_Interval, _EmissiveScroll_Width, _EmissionScrollingOffset, pos);
		}
	}

	if (_EmissionBlinkingEnabled >= 0.5)
	{
		emissionStrength0 *= calculateBlinkingEmission(_EmissiveBlink_Min, _EmissiveBlink_Max, _EmissiveBlink_Velocity, _EmissionBlinkingOffset, _EmissiveBlink_Mode, _EmissiveBlink_Duty);
	}

#if defined(PROP_EMISSIONMASK)
	float emissionMask0 = poiTex(_EmissionMask, poiUV(poiMesh.uv[int(_EmissionMaskUV)], _EmissionMask_ST) + POI_TIME.x * _EmissionMaskPan)[int(_EmissionMaskChannel)];
#else
	float emissionMask0 = 1.0;
#endif

	if (_EmissionMaskInvert >= 0.5)
	{
		emissionMask0 = 1.0 - emissionMask0;
	}

	if (_EmissionMask0GlobalMask > 0.0)
	{
		emissionMask0 = maskBlend(emissionMask0, poiMods.globalMask[int(_EmissionMask0GlobalMask) - 1], _EmissionMask0GlobalMaskBlendType);
	}

	emissionStrength0 *= emissionMask0;
}

vec3 poiEmissionLighting(vec3 emissionColor0, float emissionStrength0, in PoiLight poiLight)
{
	return poiEmissionFinish(emissionColor0, emissionStrength0, poiLight, _EmissionFluorescence, _EmissionHueShiftEnabled, _EmissionHueShift, _EmissionHueShiftSpeed, _EmissionHueShiftColorSpace, _EmissionHueSelectOrShift, _EmissionSaturation, _GITDEMinLight, _GITDEMaxLight, _GITDEMinEmissionMultiplier, _GITDEMaxEmissionMultiplier, _EnableGITDEmission, _GITDEWorldOrMesh);
}

vec3 poiEmissionLightColor(vec3 emissionColor0, in PoiLight poiLight)
{
	return poiEmissionFinishColor(emissionColor0, poiLight, _EmissionFluorescence, _EmissionHueShiftEnabled, _EmissionHueShift, _EmissionHueShiftSpeed, _EmissionHueShiftColorSpace, _EmissionHueSelectOrShift, _EmissionSaturation);
}

vec3 poiEmissionLightStrength(vec3 emissionColor0, float emissionStrength0, in PoiLight poiLight)
{
	return poiEmissionFinishStrength(emissionColor0, emissionStrength0, poiLight, _GITDEMinLight, _GITDEMaxLight, _GITDEMinEmissionMultiplier, _GITDEMaxEmissionMultiplier, _EnableGITDEmission, _GITDEWorldOrMesh);
}

bool poiEmissionUsesLight()
{
	return _EnableGITDEmission != 0.0 || _EmissionFluorescence != 0.0;
}

vec3 applyEmission(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiCam poiCam, in PoiMods poiMods)
{
	vec3 emissionColor0 = vec3(0.0);
	float emissionStrength0 = 0.0;
	poiEmissionInputs(poiFragData, poiMesh, poiLight, poiMods, emissionColor0, emissionStrength0);
	vec3 emission0 = poiEmissionLighting(emissionColor0, emissionStrength0, poiGlowUnlitLight(poiLight, poiMesh, poiCam));

	poiFragData.emission += emission0;
	return emission0 * _EmissionReplace0;
}
#endif

#if defined(POI_EMISSION_1)
void poiEmissionInputs__1(in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods, out vec3 emissionColor0__1, out float emissionStrength0__1)
{
	float emissionAlpha__1 = _EmissionColor1.a;
	emissionStrength0__1 = _EmissionStrength1;

#if defined(POI_GRABPASS)
	float mixBaseColor__1 = 0.0;
#else
	float mixBaseColor__1 = _EmissionBaseColorAsMap1;
#endif

	emissionColor0__1 = mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__1).rgb * poiThemeColor(poiMods, _EmissionColor1.rgb, _EmissionColor1ThemeIndex);
#if defined(PROP_EMISSIONMAP1)
	vec4 emissionTex__1 = vec4(0.0);
	if (_EmissionCenterOutEnabled1 < 0.5)
	{
		emissionTex__1 = poiTexPan(_EmissionMap1, poiUV(poiMesh.uv[int(_EmissionMap1UV)], _EmissionMap1_ST), _EmissionMap1Pan);
	}
	else
	{
		float centerOutValue__1 = poiLight.nDotVCentered;
		centerOutValue__1 = _EmissionCenterOutLinear1 != 0.0 ? 1.0 - acos(centerOutValue__1) * 0.63662 : centerOutValue__1;
		emissionTex__1 = poiTex(_EmissionMap1, ((0.5 + centerOutValue__1 * 0.5) * _EmissionMap1_ST.xy) + POI_TIME.x * _EmissionCenterOutSpeed1);
	}
	emissionColor0__1 = emissionTex__1.rgb * mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__1).rgb * poiThemeColor(poiMods, _EmissionColor1.rgb, _EmissionColor1ThemeIndex);
	emissionAlpha__1 = emissionAlpha__1 * emissionTex__1.a;
#endif

	emissionStrength0__1 *= emissionAlpha__1;

	if (_ScrollingEmission1 >= 0.5)
	{
		vec3 pos__1 = poiMesh.localPos;
		if (_EmissionScrollingVertexColor1 >= 0.5)
		{
			pos__1 = poiMesh.vertexColor.rgb;
		}

		if (_EmissionScrollingUseCurve1 >= 0.5)
		{
#if defined(PROP_EMISSIONSCROLLINGCURVE1)
			emissionStrength0__1 *= poiTex(_EmissionScrollingCurve1, poiUV(poiMesh.uv[int(_EmissionMap1UV)], _EmissionScrollingCurve1_ST) + (dot(pos__1, _EmissiveScroll_Direction1.xyz) * _EmissiveScroll_Interval1) + POI_TIME.x * _EmissiveScroll_Velocity1).r;
#endif
		}
		else
		{
			emissionStrength0__1 *= calculateScrollingEmission(_EmissiveScroll_Direction1.xyz, _EmissiveScroll_Velocity1, _EmissiveScroll_Interval1, _EmissiveScroll_Width1, _EmissionScrollingOffset1, pos__1);
		}
	}

	if (_EmissionBlinkingEnabled1 >= 0.5)
	{
		emissionStrength0__1 *= calculateBlinkingEmission(_EmissiveBlink_Min1, _EmissiveBlink_Max1, _EmissiveBlink_Velocity1, _EmissionBlinkingOffset1, _EmissiveBlink_Mode1, _EmissiveBlink_Duty1);
	}

#if defined(PROP_EMISSIONMASK1)
	float emissionMask0__1 = poiTex(_EmissionMask1, poiUV(poiMesh.uv[int(_EmissionMask1UV)], _EmissionMask1_ST) + POI_TIME.x * _EmissionMask1Pan)[int(_EmissionMask1Channel)];
#else
	float emissionMask0__1 = 1.0;
#endif

	if (_EmissionMaskInvert1 >= 0.5)
	{
		emissionMask0__1 = 1.0 - emissionMask0__1;
	}

	if (_EmissionMask1GlobalMask > 0.0)
	{
		emissionMask0__1 = maskBlend(emissionMask0__1, poiMods.globalMask[int(_EmissionMask1GlobalMask) - 1], _EmissionMask1GlobalMaskBlendType);
	}

	emissionStrength0__1 *= emissionMask0__1;
}

vec3 poiEmissionLighting__1(vec3 emissionColor0__1, float emissionStrength0__1, in PoiLight poiLight)
{
	return poiEmissionFinish(emissionColor0__1, emissionStrength0__1, poiLight, _EmissionFluorescence1, _EmissionHueShiftEnabled1, _EmissionHueShift1, _EmissionHueShiftSpeed1, _EmissionHueShiftColorSpace1, _EmissionHueSelectOrShift1, _EmissionSaturation1, _GITDEMinLight1, _GITDEMaxLight1, _GITDEMinEmissionMultiplier1, _GITDEMaxEmissionMultiplier1, _EnableGITDEmission1, _GITDEWorldOrMesh1);
}

vec3 poiEmissionLightColor__1(vec3 emissionColor0__1, in PoiLight poiLight)
{
	return poiEmissionFinishColor(emissionColor0__1, poiLight, _EmissionFluorescence1, _EmissionHueShiftEnabled1, _EmissionHueShift1, _EmissionHueShiftSpeed1, _EmissionHueShiftColorSpace1, _EmissionHueSelectOrShift1, _EmissionSaturation1);
}

vec3 poiEmissionLightStrength__1(vec3 emissionColor0__1, float emissionStrength0__1, in PoiLight poiLight)
{
	return poiEmissionFinishStrength(emissionColor0__1, emissionStrength0__1, poiLight, _GITDEMinLight1, _GITDEMaxLight1, _GITDEMinEmissionMultiplier1, _GITDEMaxEmissionMultiplier1, _EnableGITDEmission1, _GITDEWorldOrMesh1);
}

bool poiEmissionUsesLight__1()
{
	return _EnableGITDEmission1 != 0.0 || _EmissionFluorescence1 != 0.0;
}

vec3 applyEmission__1(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiCam poiCam, in PoiMods poiMods)
{
	vec3 emissionColor0__1 = vec3(0.0);
	float emissionStrength0__1 = 0.0;
	poiEmissionInputs__1(poiFragData, poiMesh, poiLight, poiMods, emissionColor0__1, emissionStrength0__1);
	vec3 emission0__1 = poiEmissionLighting__1(emissionColor0__1, emissionStrength0__1, poiGlowUnlitLight(poiLight, poiMesh, poiCam));

	poiFragData.emission += emission0__1;
	return emission0__1 * _EmissionReplace1;
}
#endif

#if defined(POI_EMISSION_2)
void poiEmissionInputs__2(in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods, out vec3 emissionColor0__2, out float emissionStrength0__2)
{
	float emissionAlpha__2 = _EmissionColor2.a;
	emissionStrength0__2 = _EmissionStrength2;

#if defined(POI_GRABPASS)
	float mixBaseColor__2 = 0.0;
#else
	float mixBaseColor__2 = _EmissionBaseColorAsMap2;
#endif

	emissionColor0__2 = mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__2).rgb * poiThemeColor(poiMods, _EmissionColor2.rgb, _EmissionColor2ThemeIndex);
#if defined(PROP_EMISSIONMAP2)
	vec4 emissionTex__2 = vec4(0.0);
	if (_EmissionCenterOutEnabled2 < 0.5)
	{
		emissionTex__2 = poiTexPan(_EmissionMap2, poiUV(poiMesh.uv[int(_EmissionMap2UV)], _EmissionMap2_ST), _EmissionMap2Pan);
	}
	else
	{
		float centerOutValue__2 = poiLight.nDotVCentered;
		centerOutValue__2 = _EmissionCenterOutLinear2 != 0.0 ? 1.0 - acos(centerOutValue__2) * 0.63662 : centerOutValue__2;
		emissionTex__2 = poiTex(_EmissionMap2, ((0.5 + centerOutValue__2 * 0.5) * _EmissionMap2_ST.xy) + POI_TIME.x * _EmissionCenterOutSpeed2);
	}
	emissionColor0__2 = emissionTex__2.rgb * mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__2).rgb * poiThemeColor(poiMods, _EmissionColor2.rgb, _EmissionColor2ThemeIndex);
	emissionAlpha__2 = emissionAlpha__2 * emissionTex__2.a;
#endif

	emissionStrength0__2 *= emissionAlpha__2;

	if (_ScrollingEmission2 >= 0.5)
	{
		vec3 pos__2 = poiMesh.localPos;
		if (_EmissionScrollingVertexColor2 >= 0.5)
		{
			pos__2 = poiMesh.vertexColor.rgb;
		}

		if (_EmissionScrollingUseCurve2 >= 0.5)
		{
#if defined(PROP_EMISSIONSCROLLINGCURVE2)
			emissionStrength0__2 *= poiTex(_EmissionScrollingCurve2, poiUV(poiMesh.uv[int(_EmissionMap2UV)], _EmissionScrollingCurve2_ST) + (dot(pos__2, _EmissiveScroll_Direction2.xyz) * _EmissiveScroll_Interval2) + POI_TIME.x * _EmissiveScroll_Velocity2).r;
#endif
		}
		else
		{
			emissionStrength0__2 *= calculateScrollingEmission(_EmissiveScroll_Direction2.xyz, _EmissiveScroll_Velocity2, _EmissiveScroll_Interval2, _EmissiveScroll_Width2, _EmissionScrollingOffset2, pos__2);
		}
	}

	if (_EmissionBlinkingEnabled2 >= 0.5)
	{
		emissionStrength0__2 *= calculateBlinkingEmission(_EmissiveBlink_Min2, _EmissiveBlink_Max2, _EmissiveBlink_Velocity2, _EmissionBlinkingOffset2, _EmissiveBlink_Mode2, _EmissiveBlink_Duty2);
	}

#if defined(PROP_EMISSIONMASK2)
	float emissionMask0__2 = poiTex(_EmissionMask2, poiUV(poiMesh.uv[int(_EmissionMask2UV)], _EmissionMask2_ST) + POI_TIME.x * _EmissionMask2Pan)[int(_EmissionMask2Channel)];
#else
	float emissionMask0__2 = 1.0;
#endif

	if (_EmissionMaskInvert2 >= 0.5)
	{
		emissionMask0__2 = 1.0 - emissionMask0__2;
	}

	if (_EmissionMask2GlobalMask > 0.0)
	{
		emissionMask0__2 = maskBlend(emissionMask0__2, poiMods.globalMask[int(_EmissionMask2GlobalMask) - 1], _EmissionMask2GlobalMaskBlendType);
	}

	emissionStrength0__2 *= emissionMask0__2;
}

vec3 poiEmissionLighting__2(vec3 emissionColor0__2, float emissionStrength0__2, in PoiLight poiLight)
{
	return poiEmissionFinish(emissionColor0__2, emissionStrength0__2, poiLight, _EmissionFluorescence2, _EmissionHueShiftEnabled2, _EmissionHueShift2, _EmissionHueShiftSpeed2, _EmissionHueShiftColorSpace2, _EmissionHueSelectOrShift2, _EmissionSaturation2, _GITDEMinLight2, _GITDEMaxLight2, _GITDEMinEmissionMultiplier2, _GITDEMaxEmissionMultiplier2, _EnableGITDEmission2, _GITDEWorldOrMesh2);
}

vec3 poiEmissionLightColor__2(vec3 emissionColor0__2, in PoiLight poiLight)
{
	return poiEmissionFinishColor(emissionColor0__2, poiLight, _EmissionFluorescence2, _EmissionHueShiftEnabled2, _EmissionHueShift2, _EmissionHueShiftSpeed2, _EmissionHueShiftColorSpace2, _EmissionHueSelectOrShift2, _EmissionSaturation2);
}

vec3 poiEmissionLightStrength__2(vec3 emissionColor0__2, float emissionStrength0__2, in PoiLight poiLight)
{
	return poiEmissionFinishStrength(emissionColor0__2, emissionStrength0__2, poiLight, _GITDEMinLight2, _GITDEMaxLight2, _GITDEMinEmissionMultiplier2, _GITDEMaxEmissionMultiplier2, _EnableGITDEmission2, _GITDEWorldOrMesh2);
}

bool poiEmissionUsesLight__2()
{
	return _EnableGITDEmission2 != 0.0 || _EmissionFluorescence2 != 0.0;
}

vec3 applyEmission__2(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiCam poiCam, in PoiMods poiMods)
{
	vec3 emissionColor0__2 = vec3(0.0);
	float emissionStrength0__2 = 0.0;
	poiEmissionInputs__2(poiFragData, poiMesh, poiLight, poiMods, emissionColor0__2, emissionStrength0__2);
	vec3 emission0__2 = poiEmissionLighting__2(emissionColor0__2, emissionStrength0__2, poiGlowUnlitLight(poiLight, poiMesh, poiCam));

	poiFragData.emission += emission0__2;
	return emission0__2 * _EmissionReplace2;
}
#endif

#if defined(POI_EMISSION_3)
void poiEmissionInputs__3(in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods, out vec3 emissionColor0__3, out float emissionStrength0__3)
{
	float emissionAlpha__3 = _EmissionColor3.a;
	emissionStrength0__3 = _EmissionStrength3;

#if defined(POI_GRABPASS)
	float mixBaseColor__3 = 0.0;
#else
	float mixBaseColor__3 = _EmissionBaseColorAsMap3;
#endif

	emissionColor0__3 = mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__3).rgb * poiThemeColor(poiMods, _EmissionColor3.rgb, _EmissionColor3ThemeIndex);
#if defined(PROP_EMISSIONMAP3)
	vec4 emissionTex__3 = vec4(0.0);
	if (_EmissionCenterOutEnabled3 < 0.5)
	{
		emissionTex__3 = poiTexPan(_EmissionMap3, poiUV(poiMesh.uv[int(_EmissionMap3UV)], _EmissionMap3_ST), _EmissionMap3Pan);
	}
	else
	{
		float centerOutValue__3 = poiLight.nDotVCentered;
		centerOutValue__3 = _EmissionCenterOutLinear3 != 0.0 ? 1.0 - acos(centerOutValue__3) * 0.63662 : centerOutValue__3;
		emissionTex__3 = poiTex(_EmissionMap3, ((0.5 + centerOutValue__3 * 0.5) * _EmissionMap3_ST.xy) + POI_TIME.x * _EmissionCenterOutSpeed3);
	}
	emissionColor0__3 = emissionTex__3.rgb * mix(vec3(1.0), poiFragData.baseColor, mixBaseColor__3).rgb * poiThemeColor(poiMods, _EmissionColor3.rgb, _EmissionColor3ThemeIndex);
	emissionAlpha__3 = emissionAlpha__3 * emissionTex__3.a;
#endif

	emissionStrength0__3 *= emissionAlpha__3;

	if (_ScrollingEmission3 >= 0.5)
	{
		vec3 pos__3 = poiMesh.localPos;
		if (_EmissionScrollingVertexColor3 >= 0.5)
		{
			pos__3 = poiMesh.vertexColor.rgb;
		}

		if (_EmissionScrollingUseCurve3 >= 0.5)
		{
#if defined(PROP_EMISSIONSCROLLINGCURVE3)
			emissionStrength0__3 *= poiTex(_EmissionScrollingCurve3, poiUV(poiMesh.uv[int(_EmissionMap3UV)], _EmissionScrollingCurve3_ST) + (dot(pos__3, _EmissiveScroll_Direction3.xyz) * _EmissiveScroll_Interval3) + POI_TIME.x * _EmissiveScroll_Velocity3).r;
#endif
		}
		else
		{
			emissionStrength0__3 *= calculateScrollingEmission(_EmissiveScroll_Direction3.xyz, _EmissiveScroll_Velocity3, _EmissiveScroll_Interval3, _EmissiveScroll_Width3, _EmissionScrollingOffset3, pos__3);
		}
	}

	if (_EmissionBlinkingEnabled3 >= 0.5)
	{
		emissionStrength0__3 *= calculateBlinkingEmission(_EmissiveBlink_Min3, _EmissiveBlink_Max3, _EmissiveBlink_Velocity3, _EmissionBlinkingOffset3, _EmissiveBlink_Mode3, _EmissiveBlink_Duty3);
	}

#if defined(PROP_EMISSIONMASK3)
	float emissionMask0__3 = poiTex(_EmissionMask3, poiUV(poiMesh.uv[int(_EmissionMask3UV)], _EmissionMask3_ST) + POI_TIME.x * _EmissionMask3Pan)[int(_EmissionMask3Channel)];
#else
	float emissionMask0__3 = 1.0;
#endif

	if (_EmissionMaskInvert3 >= 0.5)
	{
		emissionMask0__3 = 1.0 - emissionMask0__3;
	}

	if (_EmissionMask3GlobalMask > 0.0)
	{
		emissionMask0__3 = maskBlend(emissionMask0__3, poiMods.globalMask[int(_EmissionMask3GlobalMask) - 1], _EmissionMask3GlobalMaskBlendType);
	}

	emissionStrength0__3 *= emissionMask0__3;
}

vec3 poiEmissionLighting__3(vec3 emissionColor0__3, float emissionStrength0__3, in PoiLight poiLight)
{
	return poiEmissionFinish(emissionColor0__3, emissionStrength0__3, poiLight, _EmissionFluorescence3, _EmissionHueShiftEnabled3, _EmissionHueShift3, _EmissionHueShiftSpeed3, _EmissionHueShiftColorSpace3, _EmissionHueSelectOrShift3, _EmissionSaturation3, _GITDEMinLight3, _GITDEMaxLight3, _GITDEMinEmissionMultiplier3, _GITDEMaxEmissionMultiplier3, _EnableGITDEmission3, _GITDEWorldOrMesh3);
}

vec3 poiEmissionLightColor__3(vec3 emissionColor0__3, in PoiLight poiLight)
{
	return poiEmissionFinishColor(emissionColor0__3, poiLight, _EmissionFluorescence3, _EmissionHueShiftEnabled3, _EmissionHueShift3, _EmissionHueShiftSpeed3, _EmissionHueShiftColorSpace3, _EmissionHueSelectOrShift3, _EmissionSaturation3);
}

vec3 poiEmissionLightStrength__3(vec3 emissionColor0__3, float emissionStrength0__3, in PoiLight poiLight)
{
	return poiEmissionFinishStrength(emissionColor0__3, emissionStrength0__3, poiLight, _GITDEMinLight3, _GITDEMaxLight3, _GITDEMinEmissionMultiplier3, _GITDEMaxEmissionMultiplier3, _EnableGITDEmission3, _GITDEWorldOrMesh3);
}

bool poiEmissionUsesLight__3()
{
	return _EnableGITDEmission3 != 0.0 || _EmissionFluorescence3 != 0.0;
}

vec3 applyEmission__3(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiLight poiLight, in PoiCam poiCam, in PoiMods poiMods)
{
	vec3 emissionColor0__3 = vec3(0.0);
	float emissionStrength0__3 = 0.0;
	poiEmissionInputs__3(poiFragData, poiMesh, poiLight, poiMods, emissionColor0__3, emissionStrength0__3);
	vec3 emission0__3 = poiEmissionLighting__3(emissionColor0__3, emissionStrength0__3, poiGlowUnlitLight(poiLight, poiMesh, poiCam));

	poiFragData.emission += emission0__3;
	return emission0__3 * _EmissionReplace3;
}
#endif

#if defined(POI_EMISSION_0) || defined(POI_EMISSION_1) || defined(POI_EMISSION_2) || defined(POI_EMISSION_3)
// Glow in the dark and fluorescence follow Poiyomi's main light: directional lights only. The base color
// replace is redone with the lit emission. Slots without a light-dependent term still enter the replace sum.
// Without fluorescence the color is the same lit and unlit, so its hue shift runs once. -xlinka
#if defined(POI_EMISSION_READS_LIGHT)
#define POI_HAS_LIGHT_HOOK_EMISSION
#endif
void applyEmissionLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	if (!poiLight.isDirectional)
	{
		return;
	}
	bool usesLight = false;
#if defined(POI_EMISSION_0)
	usesLight = usesLight || poiEmissionUsesLight();
#endif
#if defined(POI_EMISSION_1)
	usesLight = usesLight || poiEmissionUsesLight__1();
#endif
#if defined(POI_EMISSION_2)
	usesLight = usesLight || poiEmissionUsesLight__2();
#endif
#if defined(POI_EMISSION_3)
	usesLight = usesLight || poiEmissionUsesLight__3();
#endif
	if (!usesLight)
	{
		return;
	}

	PoiLight unlitLight = poiGlowUnlitLight(poiLight, poiMesh, poiCam);
	vec3 emissionLit = vec3(0.0);
	vec3 emissionUnlit = vec3(0.0);
	vec3 emissionBaseReplaceLit = vec3(0.0);
	vec3 emissionBaseReplaceUnlit = vec3(0.0);
	vec3 emissionColor = vec3(0.0);
	float emissionStrength = 0.0;
	vec3 emissionWithLight = vec3(0.0);
	vec3 emissionWithoutLight = vec3(0.0);

#if defined(POI_EMISSION_0)
	if (poiEmissionUsesLight() || _EmissionReplace0 != 0.0)
	{
		poiEmissionInputs(poiFragData, poiMesh, poiLight, poiMods, emissionColor, emissionStrength);
		vec3 colorWithLight = poiEmissionLightColor(emissionColor, poiLight);
		vec3 colorWithoutLight = colorWithLight;
		if (_EmissionFluorescence != 0.0)
		{
			colorWithoutLight = poiEmissionLightColor(emissionColor, unlitLight);
		}
		emissionWithLight = poiEmissionLightStrength(colorWithLight, emissionStrength, poiLight);
		emissionWithoutLight = poiEmissionLightStrength(colorWithoutLight, emissionStrength, unlitLight);
		emissionLit += emissionWithLight;
		emissionUnlit += emissionWithoutLight;
		emissionBaseReplaceLit += emissionWithLight * _EmissionReplace0;
		emissionBaseReplaceUnlit += emissionWithoutLight * _EmissionReplace0;
	}
#endif
#if defined(POI_EMISSION_1)
	if (poiEmissionUsesLight__1() || _EmissionReplace1 != 0.0)
	{
		poiEmissionInputs__1(poiFragData, poiMesh, poiLight, poiMods, emissionColor, emissionStrength);
		vec3 colorWithLight = poiEmissionLightColor__1(emissionColor, poiLight);
		vec3 colorWithoutLight = colorWithLight;
		if (_EmissionFluorescence1 != 0.0)
		{
			colorWithoutLight = poiEmissionLightColor__1(emissionColor, unlitLight);
		}
		emissionWithLight = poiEmissionLightStrength__1(colorWithLight, emissionStrength, poiLight);
		emissionWithoutLight = poiEmissionLightStrength__1(colorWithoutLight, emissionStrength, unlitLight);
		emissionLit += emissionWithLight;
		emissionUnlit += emissionWithoutLight;
		emissionBaseReplaceLit += emissionWithLight * _EmissionReplace1;
		emissionBaseReplaceUnlit += emissionWithoutLight * _EmissionReplace1;
	}
#endif
#if defined(POI_EMISSION_2)
	if (poiEmissionUsesLight__2() || _EmissionReplace2 != 0.0)
	{
		poiEmissionInputs__2(poiFragData, poiMesh, poiLight, poiMods, emissionColor, emissionStrength);
		vec3 colorWithLight = poiEmissionLightColor__2(emissionColor, poiLight);
		vec3 colorWithoutLight = colorWithLight;
		if (_EmissionFluorescence2 != 0.0)
		{
			colorWithoutLight = poiEmissionLightColor__2(emissionColor, unlitLight);
		}
		emissionWithLight = poiEmissionLightStrength__2(colorWithLight, emissionStrength, poiLight);
		emissionWithoutLight = poiEmissionLightStrength__2(colorWithoutLight, emissionStrength, unlitLight);
		emissionLit += emissionWithLight;
		emissionUnlit += emissionWithoutLight;
		emissionBaseReplaceLit += emissionWithLight * _EmissionReplace2;
		emissionBaseReplaceUnlit += emissionWithoutLight * _EmissionReplace2;
	}
#endif
#if defined(POI_EMISSION_3)
	if (poiEmissionUsesLight__3() || _EmissionReplace3 != 0.0)
	{
		poiEmissionInputs__3(poiFragData, poiMesh, poiLight, poiMods, emissionColor, emissionStrength);
		vec3 colorWithLight = poiEmissionLightColor__3(emissionColor, poiLight);
		vec3 colorWithoutLight = colorWithLight;
		if (_EmissionFluorescence3 != 0.0)
		{
			colorWithoutLight = poiEmissionLightColor__3(emissionColor, unlitLight);
		}
		emissionWithLight = poiEmissionLightStrength__3(colorWithLight, emissionStrength, poiLight);
		emissionWithoutLight = poiEmissionLightStrength__3(colorWithoutLight, emissionStrength, unlitLight);
		emissionLit += emissionWithLight;
		emissionUnlit += emissionWithoutLight;
		emissionBaseReplaceLit += emissionWithLight * _EmissionReplace3;
		emissionBaseReplaceUnlit += emissionWithoutLight * _EmissionReplace3;
	}
#endif

	vec3 baseLit = mix(poiFragData.baseColor.rgb, clamp(emissionBaseReplaceLit, 0.0, 1.0), poiMax(emissionBaseReplaceLit));
	vec3 baseUnlit = mix(poiFragData.baseColor.rgb, clamp(emissionBaseReplaceUnlit, 0.0, 1.0), poiMax(emissionBaseReplaceUnlit));
	poiGlowAddLightShare(poiLight, baseLit - baseUnlit, (emissionLit - emissionUnlit) * poiMods.globalEmission * _PPEmissionMultiplier);
}
#endif
