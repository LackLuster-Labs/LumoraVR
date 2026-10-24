// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Each rim style has a core that takes the light state to read. The entry points FRAG calls in
// fragment() run it with poiGlowUnlitLight; applyRimLightingLight and applyDepthRimLightingLight run it
// again per light, with and without the light, on copies of the structs, and add the difference
// (PoiEmission.gdshaderinc). Parameter types follow the uniforms FRAG passes. -xlinka

#if defined(POI_RIM_0) || defined(POI_RIM_1)
#if defined(POI_RIM_0_POIYOMI) || defined(POI_RIM_1_POIYOMI)
void poiApplyPoiyomiRim(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, inout PoiMods poiMods, float Is_NormalMapToRimLight, float RimInvert, float RimPower, float RimLinear, float RimStrength, float RimShadowWidth, float RimShadowToggle, float RimWidth, float RimBlendStrength, float RimMask, float RimGlobalMask, float RimGlobalMaskBlendType, vec4 RimTex, vec4 RimLightColor, float RimLightColorThemeIndex, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed, float RimBlur, float RimShadowMaskRampType, float RimShadowMaskInvert, float RimShadowMaskStrength, vec2 RimShadowAlpha, float RimApplyGlobalMaskIndex, float RimApplyGlobalMaskBlendType, float RimBaseColorMix, float RimBrightness, float RimBlendMode, float rimBias, float rimBiasIntensity, int RimApplyAlpha, float RimApplyAlphaBlend)
{
	float viewDotNormal = abs(dot(poiCam.viewDir, mix(poiMesh.normals[0], poiMesh.normals[1], Is_NormalMapToRimLight)));
	viewDotNormal = RimLinear != 0.0 ? 1.0 - acos(viewDotNormal) * 0.63662 : viewDotNormal;

	if (RimInvert != 0.0)
	{
		viewDotNormal = 1.0 - viewDotNormal;
	}

	viewDotNormal = pow(viewDotNormal, RimPower);

	if (RimShadowWidth != 0.0 && RimShadowToggle != 0.0)
	{
		viewDotNormal += mix(0.0, (1.0 - poiLight.nDotLNormalized) * 3.0, RimShadowWidth);
	}

	viewDotNormal *= mix(1.0, rimBias, rimBiasIntensity);

	float rimStrength = RimStrength;

	float rimWidth = mix(-0.05, 1.0, RimWidth);

	float blendStrength = RimBlendStrength;
	float rimMask = RimMask;

	if (RimGlobalMask > 0.0)
	{
		rimMask = maskBlend(rimMask, poiMods.globalMask[int(RimGlobalMask) - 1], RimGlobalMaskBlendType);
	}

	vec4 rimColor = RimTex;
	rimColor *= vec4(poiThemeColor(poiMods, RimLightColor.rgb, RimLightColorThemeIndex), RimLightColor.a);

	if (RimHueShiftEnabled != 0.0)
	{
		rimColor.rgb = hueShift(rimColor.rgb, RimHueShift + POI_TIME.x * RimHueShiftSpeed, RimHueShiftColorSpace, RimHueSelectOrShift);
	}

	float fw = fwidth(viewDotNormal);
	float borderMin = clamp(rimWidth - RimBlur, 0.0, 1.0);
	float range = rimWidth - borderMin;

	// A Width of 0 maps rimWidth to -0.05 in a dead-zone below the silhouette; a non-positive width is off,
	// or the fwidth term would still leak a sub-pixel rim at the silhouette.
	float rim = (rimWidth <= 0.0) ? 0.0 : 1.0 - clamp((viewDotNormal - borderMin) / max(range + min(fw, range), 0.0001), 0.0, 1.0);

	rim *= RimLightColor.a * rimColor.a * rimMask;

	if (RimShadowToggle != 0.0)
	{
		switch (int(RimShadowMaskRampType))
		{
			case 0:
			{
				float rampedLightMap = poiLight.rampedLightMap.x;
				if (RimShadowMaskInvert != 0.0) rampedLightMap = 1.0 - rampedLightMap;
				rim = mix(rim, rim * rampedLightMap, RimShadowMaskStrength);
				break;
			}
			case 1:
			{
				float nDotLNormalized = poiLight.nDotLNormalized;
				if (RimShadowMaskInvert != 0.0) nDotLNormalized = 1.0 - nDotLNormalized;
				rim = mix(rim, rim * smoothstep(RimShadowAlpha.x, RimShadowAlpha.y, nDotLNormalized), RimShadowMaskStrength);
				break;
			}
		}
	}

	if (RimApplyGlobalMaskIndex > 0.0)
	{
		applyToGlobalMask(poiMods, RimApplyGlobalMaskIndex - 1.0, RimApplyGlobalMaskBlendType, rim * blendStrength);
	}

	if (RimApplyAlpha == 1) // Add
	{
		poiFragData.alpha += mix(0.0, clamp(rim, 0.0, 1.0), RimApplyAlphaBlend);
		poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
	}
	if (RimApplyAlpha == 2) // Multiply
	{
		poiFragData.alpha *= mix(1.0, clamp(rim, 0.0, 1.0), RimApplyAlphaBlend);
	}

#if defined(POI_GRABPASS)
	vec3 finalRimColor = rimColor.rgb;
#else
	vec3 finalRimColor = rimColor.rgb * mix(vec3(1.0), poiFragData.baseColor, RimBaseColorMix);
#endif

	finalRimColor *= RimBrightness;
	// Add 0, Replace 1, Multiply 2, Mixed 3
	switch (int(RimBlendMode))
	{
		case 0: { poiFragData.baseColor += finalRimColor * rim * blendStrength; break; }
		case 1: { poiFragData.baseColor = mix(poiFragData.baseColor, finalRimColor, rim * blendStrength); break; }
		case 2: { poiFragData.baseColor = mix(poiFragData.baseColor, poiFragData.baseColor * finalRimColor, rim * blendStrength); break; }
		case 3: { poiFragData.baseColor = mix(poiFragData.baseColor.rgb, poiFragData.baseColor.rgb + poiFragData.baseColor.rgb * finalRimColor, rim * blendStrength); break; }
		case 4: { poiFragData.baseColor = mix(poiFragData.baseColor, 1.0 - (1.0 - poiFragData.baseColor) * (1.0 - finalRimColor), rim * blendStrength); break; }
	}
	poiFragData.emission += finalRimColor * rim * rimStrength;
}

void ApplyPoiyomiRimLighting(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, inout PoiMods poiMods, float Is_NormalMapToRimLight, float RimInvert, float RimPower, float RimLinear, float RimStrength, float RimShadowWidth, float RimShadowToggle, float RimWidth, float RimBlendStrength, float RimMask, float RimGlobalMask, float RimGlobalMaskBlendType, vec4 RimTex, vec4 RimLightColor, float RimLightColorThemeIndex, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed, float RimBlur, float RimShadowMaskRampType, float RimShadowMaskInvert, float RimShadowMaskStrength, vec2 RimShadowAlpha, float RimApplyGlobalMaskIndex, float RimApplyGlobalMaskBlendType, float RimBaseColorMix, float RimBrightness, float RimBlendMode, float rimBias, float rimBiasIntensity, int RimApplyAlpha, float RimApplyAlphaBlend)
{
	poiApplyPoiyomiRim(poiFragData, poiMesh, poiCam, poiGlowUnlitLight(poiLight, poiMesh, poiCam), poiMods, Is_NormalMapToRimLight, RimInvert, RimPower, RimLinear, RimStrength, RimShadowWidth, RimShadowToggle, RimWidth, RimBlendStrength, RimMask, RimGlobalMask, RimGlobalMaskBlendType, RimTex, RimLightColor, RimLightColorThemeIndex, RimHueShiftEnabled, RimHueShift, RimHueShiftColorSpace, RimHueSelectOrShift, RimHueShiftSpeed, RimBlur, RimShadowMaskRampType, RimShadowMaskInvert, RimShadowMaskStrength, RimShadowAlpha, RimApplyGlobalMaskIndex, RimApplyGlobalMaskBlendType, RimBaseColorMix, RimBrightness, RimBlendMode, rimBias, rimBiasIntensity, RimApplyAlpha, RimApplyAlphaBlend);
}
#endif

#if defined(POI_RIM_0_UTS2) || defined(POI_RIM_1_UTS2)
void poiApplyUTS2Rim(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods, float Set_RimLightMask_var, float RimGlobalMask, float RimGlobalMaskBlendType, vec4 RimLightColor, float RimLightColorThemeIndex, float Is_LightColor_RimLight, float Is_NormalMapToRimLight, float RimLight_Power, float RimLight_InsideMask, float RimLight_FeatherOff, float LightDirection_MaskOn, float Tweak_LightDirection_MaskLevel, float Add_Antipodean_RimLight, vec4 Ap_RimLightColor, float RimApColorThemeIndex, float Is_LightColor_Ap_RimLight, float Ap_RimLight_Power, float Ap_RimLight_FeatherOff, float Tweak_RimLightMaskLevel, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed)
{
	if (RimGlobalMask > 0.0)
	{
		Set_RimLightMask_var = maskBlend(Set_RimLightMask_var, poiMods.globalMask[int(RimGlobalMask) - 1], RimGlobalMaskBlendType);
	}

	vec3 rimColor = vec3(poiThemeColor(poiMods, RimLightColor.rgb, RimLightColorThemeIndex));
	vec3 _Is_LightColor_RimLight_var = mix(rimColor, (rimColor * poiLight.directColor), Is_LightColor_RimLight);
	float _RimArea_var = (1.0 - dot(mix(poiMesh.normals[0], poiMesh.normals[1], Is_NormalMapToRimLight), poiCam.viewDir));
	float _RimLightPower_var = pow(_RimArea_var, exp2(mix(3.0, 0.0, RimLight_Power)));
	float _Rimlight_InsideMask_var = clamp(mix((0.0 + ((_RimLightPower_var - RimLight_InsideMask) * (1.0 - 0.0)) / (1.0 - RimLight_InsideMask)), step(RimLight_InsideMask, _RimLightPower_var), RimLight_FeatherOff), 0.0, 1.0);
	float _VertHalfLambert_var = 0.5 * dot(poiMesh.normals[0], poiLight.direction) + 0.5;
	vec3 _LightDirection_MaskOn_var = mix((_Is_LightColor_RimLight_var * _Rimlight_InsideMask_var), (_Is_LightColor_RimLight_var * clamp((_Rimlight_InsideMask_var - ((1.0 - _VertHalfLambert_var) + Tweak_LightDirection_MaskLevel)), 0.0, 1.0)), LightDirection_MaskOn);
	float _ApRimLightPower_var = pow(_RimArea_var, exp2(mix(3.0, 0.0, Ap_RimLight_Power)));
	vec3 ApRimColor = vec3(poiThemeColor(poiMods, Ap_RimLightColor.rgb, RimApColorThemeIndex));
	vec3 _RimLight_var = (clamp((Set_RimLightMask_var + Tweak_RimLightMaskLevel), 0.0, 1.0) * mix(_LightDirection_MaskOn_var, (_LightDirection_MaskOn_var + (mix(ApRimColor, (ApRimColor * poiLight.directColor), Is_LightColor_Ap_RimLight) * clamp((mix((0.0 + ((_ApRimLightPower_var - RimLight_InsideMask) * (1.0 - 0.0)) / (1.0 - RimLight_InsideMask)), step(RimLight_InsideMask, _ApRimLightPower_var), Ap_RimLight_FeatherOff) - (clamp(_VertHalfLambert_var, 0.0, 1.0) + Tweak_LightDirection_MaskLevel)), 0.0, 1.0))), Add_Antipodean_RimLight));
	if (RimHueShiftEnabled != 0.0)
	{
		_RimLight_var = hueShift(_RimLight_var, RimHueShift + POI_TIME.x * RimHueShiftSpeed, RimHueShiftColorSpace, RimHueSelectOrShift);
	}
	poiFragData.baseColor += _RimLight_var;
}

void ApplyUTS2RimLighting(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods, float Set_RimLightMask_var, float RimGlobalMask, float RimGlobalMaskBlendType, vec4 RimLightColor, float RimLightColorThemeIndex, float Is_LightColor_RimLight, float Is_NormalMapToRimLight, float RimLight_Power, float RimLight_InsideMask, float RimLight_FeatherOff, float LightDirection_MaskOn, float Tweak_LightDirection_MaskLevel, float Add_Antipodean_RimLight, vec4 Ap_RimLightColor, float RimApColorThemeIndex, float Is_LightColor_Ap_RimLight, float Ap_RimLight_Power, float Ap_RimLight_FeatherOff, float Tweak_RimLightMaskLevel, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed)
{
	poiApplyUTS2Rim(poiFragData, poiMesh, poiCam, poiGlowUnlitLight(poiLight, poiMesh, poiCam), poiMods, Set_RimLightMask_var, RimGlobalMask, RimGlobalMaskBlendType, RimLightColor, RimLightColorThemeIndex, Is_LightColor_RimLight, Is_NormalMapToRimLight, RimLight_Power, RimLight_InsideMask, RimLight_FeatherOff, LightDirection_MaskOn, Tweak_LightDirection_MaskLevel, Add_Antipodean_RimLight, Ap_RimLightColor, RimApColorThemeIndex, Is_LightColor_Ap_RimLight, Ap_RimLight_Power, Ap_RimLight_FeatherOff, Tweak_RimLightMaskLevel, RimHueShiftEnabled, RimHueShift, RimHueShiftColorSpace, RimHueSelectOrShift, RimHueShiftSpeed);
}
#endif

#if defined(POI_RIM_0_LILTOON) || defined(POI_RIM_1_LILTOON)
// Poiyomi runs this after lighting on finalColor, scaling the rim color by 1 - E + finalLighting * E
// (E = RimEnableLighting). The finalLighting share joins the lit base color (Godot lights it), the rest
// the unlit color (poiFragData.finalColor). Normal and Add split exactly; Screen applies to each part
// on its own; Multiply leaves the rim color unlit, as Poiyomi's add pass does.
void poiLiltoonRimBlend(inout PoiFragData poiFragData, vec3 rimColorLit, vec3 rimColorUnlit, float rimAlpha, int RimBlendMode)
{
	if (RimBlendMode == 3)
	{
		poiFragData.baseColor = lilBlendColor(poiFragData.baseColor, rimColorLit + rimColorUnlit, rimAlpha, RimBlendMode);
		poiFragData.finalColor = lilBlendColor(poiFragData.finalColor, rimColorLit + rimColorUnlit, rimAlpha, RimBlendMode);
	}
	else
	{
		poiFragData.baseColor = lilBlendColor(poiFragData.baseColor, rimColorLit, rimAlpha, RimBlendMode);
		poiFragData.finalColor = lilBlendColor(poiFragData.finalColor, rimColorUnlit, rimAlpha, RimBlendMode);
	}
}

void poiApplyLiltoonRim(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods, vec4 RimColor, vec4 RimIndirColor, vec4 RimColorTex, float RimMainStrength, float RimNormalStrength, float RimDirRange, float RimIndirRange, float RimFresnelPower, int RimBackfaceMask, float RimDirStrength, float RimBorder, float RimBlur, float RimIndirBorder, float RimIndirBlur, float RimShadowMask, float RimEnableLighting, float RimVRParallaxStrength, float RimGlobalMask, float RimGlobalMaskBlendType, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed, int RimBlendMode, float RimTexMaskOnly, float RimLightColorThemeIndex)
{
	RimColor.rgb = vec3(poiThemeColor(poiMods, RimColor.rgb, RimLightColorThemeIndex));

	if (RimGlobalMask > 0.0)
	{
		RimColorTex.a = maskBlend(RimColorTex.a, poiMods.globalMask[int(RimGlobalMask) - 1], RimGlobalMaskBlendType);
	}
	vec4 rimColor = RimColor;
	vec4 rimIndirColor = RimIndirColor;
	if (RimTexMaskOnly > 0.0)
	{
		rimColor.a *= RimColorTex.r;
		rimIndirColor.a *= RimColorTex.r;
	}
	else
	{
		rimColor *= RimColorTex;
		rimIndirColor *= RimColorTex;
	}

	if (RimHueShiftEnabled != 0.0)
	{
		rimColor.rgb = hueShift(rimColor.rgb, RimHueShift + POI_TIME.x * RimHueShiftSpeed, RimHueShiftColorSpace, RimHueSelectOrShift);
		rimIndirColor.rgb = hueShift(rimIndirColor.rgb, RimHueShift + POI_TIME.x * RimHueShiftSpeed, RimHueShiftColorSpace, RimHueSelectOrShift);
	}

	rimColor.rgb = mix(rimColor.rgb, rimColor.rgb * poiFragData.baseColor, RimMainStrength);

	// View direction
	vec3 viewDir = mix(poiCam.viewDir, poiCam.eyeViewDir, RimVRParallaxStrength);

	// Normal
	vec3 normal = mix(poiMesh.normals[0], poiMesh.normals[1], RimNormalStrength);
	float nvabs = abs(dot(normal, viewDir));

	// Factor
	float lnRaw = dot(poiLight.direction, normal) * 0.5 + 0.5;
	float lnDir = clamp((lnRaw + RimDirRange) / (1.0 + RimDirRange), 0.0, 1.0);
	float lnIndir = clamp((1.0 - lnRaw + RimIndirRange) / (1.0 + RimIndirRange), 0.0, 1.0);
	float rim = pow(clamp(1.0 - nvabs, 0.0, 1.0), RimFresnelPower);
	rim = (poiMesh.isFrontFace == 0.0 && RimBackfaceMask != 0) ? 0.0 : rim;
	float rimDir = mix(rim, rim * lnDir, RimDirStrength);
	float rimIndir = rim * lnIndir * RimDirStrength;

	rimDir = poiEdgeLinear(rimDir, RimBorder, RimBlur);
	rimIndir = poiEdgeLinear(rimIndir, RimIndirBorder, RimIndirBlur);

	rimDir = mix(rimDir, rimDir * poiLight.rampedLightMap.x, RimShadowMask);
	rimIndir = mix(rimIndir, rimIndir * poiLight.rampedLightMap.x, RimShadowMask);

	poiLiltoonRimBlend(poiFragData, rimColor.rgb * RimEnableLighting, rimColor.rgb * (1.0 - RimEnableLighting), rimDir * rimColor.a, RimBlendMode);
	poiLiltoonRimBlend(poiFragData, rimIndirColor.rgb * RimEnableLighting, rimIndirColor.rgb * (1.0 - RimEnableLighting), rimIndir * rimIndirColor.a, RimBlendMode);
}

void ApplyLiltoonRimLighting(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods, vec4 RimColor, vec4 RimIndirColor, vec4 RimColorTex, float RimMainStrength, float RimNormalStrength, float RimDirRange, float RimIndirRange, float RimFresnelPower, int RimBackfaceMask, float RimDirStrength, float RimBorder, float RimBlur, float RimIndirBorder, float RimIndirBlur, float RimShadowMask, float RimEnableLighting, float RimVRParallaxStrength, float RimGlobalMask, float RimGlobalMaskBlendType, float RimHueShiftEnabled, float RimHueShift, float RimHueShiftColorSpace, float RimHueSelectOrShift, float RimHueShiftSpeed, int RimBlendMode, float RimTexMaskOnly, float RimLightColorThemeIndex)
{
	poiApplyLiltoonRim(poiFragData, poiMesh, poiCam, poiGlowUnlitLight(poiLight, poiMesh, poiCam), poiMods, RimColor, RimIndirColor, RimColorTex, RimMainStrength, RimNormalStrength, RimDirRange, RimIndirRange, RimFresnelPower, RimBackfaceMask, RimDirStrength, RimBorder, RimBlur, RimIndirBorder, RimIndirBlur, RimShadowMask, RimEnableLighting, RimVRParallaxStrength, RimGlobalMask, RimGlobalMaskBlendType, RimHueShiftEnabled, RimHueShift, RimHueShiftColorSpace, RimHueSelectOrShift, RimHueShiftSpeed, RimBlendMode, RimTexMaskOnly, RimLightColorThemeIndex);
}
#endif

// Per light: a rim's share is its core run with this light minus with the unlit state.
#if defined(POI_RIM_0)
void poiRimLightingLight0(inout PoiLight poiLight, in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiMods poiMods)
{
	bool readsLight = false;
#if defined(POI_RIM_0_POIYOMI)
	readsLight = readsLight || _RimShadowToggle != 0.0;
#endif
#if defined(POI_RIM_0_UTS2)
	readsLight = readsLight || _Is_LightColor_RimLight != 0.0 || _LightDirection_MaskOn != 0.0 || _Add_Antipodean_RimLight != 0.0;
#endif
#if defined(POI_RIM_0_LILTOON)
	readsLight = readsLight || _RimDirStrength != 0.0 || _RimShadowMask != 0.0;
#endif
	if (!readsLight)
	{
		return;
	}
	PoiLight unlitLight = poiGlowUnlitLight(poiLight, poiMesh, poiCam);
	PoiFragData fragLit = poiFragData;
	PoiFragData fragUnlit = poiFragData;
	fragLit.emission = vec3(0.0);
	fragUnlit.emission = vec3(0.0);
	fragLit.finalColor = vec3(0.0);
	fragUnlit.finalColor = vec3(0.0);
	PoiMods modsLit = poiMods;
	PoiMods modsUnlit = poiMods;
	bool usesLight = false;

#if defined(POI_RIM_0_POIYOMI)
	if (_RimShadowToggle != 0.0)
	{
		usesLight = true;
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
		poiApplyPoiyomiRim(fragLit, poiMesh, poiCam, poiLight, modsLit, _Is_NormalMapToRimLight, _RimLightingInvert, _RimPower, _RimLinear, _RimStrength, _RimShadowWidth, _RimShadowToggle, _RimWidth, _RimBlendStrength, rimMask, _RimGlobalMask, _RimGlobalMaskBlendType, rimColor, _RimLightColor, _RimLightColorThemeIndex, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlur, _RimShadowMaskRampType, _RimShadowMaskInvert, _RimShadowMaskStrength, _RimShadowAlpha.xy, _RimApplyGlobalMaskIndex, _RimApplyGlobalMaskBlendType, _RimBaseColorMix, _RimBrightness, _RimPoiBlendMode, rimBias, _RimBiasIntensity, _RimApplyAlpha, _RimApplyAlphaBlend);
		poiApplyPoiyomiRim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, _Is_NormalMapToRimLight, _RimLightingInvert, _RimPower, _RimLinear, _RimStrength, _RimShadowWidth, _RimShadowToggle, _RimWidth, _RimBlendStrength, rimMask, _RimGlobalMask, _RimGlobalMaskBlendType, rimColor, _RimLightColor, _RimLightColorThemeIndex, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlur, _RimShadowMaskRampType, _RimShadowMaskInvert, _RimShadowMaskStrength, _RimShadowAlpha.xy, _RimApplyGlobalMaskIndex, _RimApplyGlobalMaskBlendType, _RimBaseColorMix, _RimBrightness, _RimPoiBlendMode, rimBias, _RimBiasIntensity, _RimApplyAlpha, _RimApplyAlphaBlend);
	}
#endif

#if defined(POI_RIM_0_UTS2)
	if (_Is_LightColor_RimLight != 0.0 || _LightDirection_MaskOn != 0.0 || _Add_Antipodean_RimLight != 0.0)
	{
		usesLight = true;
#if defined(PROP_SET_RIMLIGHTMASK)
		float Set_RimLightMask_var = poiTexPan(_Set_RimLightMask, poiUV(poiMesh.uv[int(_Set_RimLightMaskUV)], _Set_RimLightMask_ST), _Set_RimLightMaskPan)[int(_Set_RimLightMaskChannel)];
#else
		float Set_RimLightMask_var = 1.0;
#endif
		poiApplyUTS2Rim(fragLit, poiMesh, poiCam, poiLight, modsLit, Set_RimLightMask_var, _RimGlobalMask, _RimGlobalMaskBlendType, _RimLightColor, _RimLightColorThemeIndex, _Is_LightColor_RimLight, _Is_NormalMapToRimLight, _RimLight_Power, _RimLight_InsideMask, _RimLight_FeatherOff, _LightDirection_MaskOn, _Tweak_LightDirection_MaskLevel, _Add_Antipodean_RimLight, _Ap_RimLightColor, _RimApColorThemeIndex, _Is_LightColor_Ap_RimLight, _Ap_RimLight_Power, _Ap_RimLight_FeatherOff, _Tweak_RimLightMaskLevel, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed);
		poiApplyUTS2Rim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, Set_RimLightMask_var, _RimGlobalMask, _RimGlobalMaskBlendType, _RimLightColor, _RimLightColorThemeIndex, _Is_LightColor_RimLight, _Is_NormalMapToRimLight, _RimLight_Power, _RimLight_InsideMask, _RimLight_FeatherOff, _LightDirection_MaskOn, _Tweak_LightDirection_MaskLevel, _Add_Antipodean_RimLight, _Ap_RimLightColor, _RimApColorThemeIndex, _Is_LightColor_Ap_RimLight, _Ap_RimLight_Power, _Ap_RimLight_FeatherOff, _Tweak_RimLightMaskLevel, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed);
	}
#endif

#if defined(POI_RIM_0_LILTOON)
	if (_RimDirStrength != 0.0 || _RimShadowMask != 0.0)
	{
		usesLight = true;
#if defined(PROP_RIMCOLORTEX)
		vec4 rimColorTex = poiTexPan(_RimColorTex, poiUV(poiMesh.uv[int(_RimColorTexUV)], _RimColorTex_ST), _RimColorTexPan);
#else
		vec4 rimColorTex = vec4(1.0);
#endif
		poiApplyLiltoonRim(fragLit, poiMesh, poiCam, poiLight, modsLit, _RimColor, _RimIndirColor, rimColorTex, _RimMainStrength, _RimNormalStrength, _RimDirRange, _RimIndirRange, _RimFresnelPower, _RimBackfaceMask, _RimDirStrength, _RimBorder, _RimBlur, _RimIndirBorder, _RimIndirBlur, _RimShadowMask, _RimEnableLighting, _RimVRParallaxStrength, _RimGlobalMask, _RimGlobalMaskBlendType, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlendMode, _RimMaskOnlyMask, _RimLightColorThemeIndex);
		poiApplyLiltoonRim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, _RimColor, _RimIndirColor, rimColorTex, _RimMainStrength, _RimNormalStrength, _RimDirRange, _RimIndirRange, _RimFresnelPower, _RimBackfaceMask, _RimDirStrength, _RimBorder, _RimBlur, _RimIndirBorder, _RimIndirBlur, _RimShadowMask, _RimEnableLighting, _RimVRParallaxStrength, _RimGlobalMask, _RimGlobalMaskBlendType, _RimHueShiftEnabled, _RimHueShift, _RimHueShiftColorSpace, _RimHueSelectOrShift, _RimHueShiftSpeed, _RimBlendMode, _RimMaskOnlyMask, _RimLightColorThemeIndex);
	}
#endif

	if (usesLight)
	{
		poiGlowAddLightShare(poiLight, fragLit.baseColor - fragUnlit.baseColor, (fragLit.emission - fragUnlit.emission) * poiMods.globalEmission * _PPEmissionMultiplier + (fragLit.finalColor - fragUnlit.finalColor));
	}
}
#endif

#if defined(POI_RIM_1)
void poiRimLightingLight1(inout PoiLight poiLight, in PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiMods poiMods)
{
	bool readsLight = false;
#if defined(POI_RIM_1_POIYOMI)
	readsLight = readsLight || _Rim2ShadowToggle != 0.0;
#endif
#if defined(POI_RIM_1_UTS2)
	readsLight = readsLight || _Is_LightColor_Rim2Light != 0.0 || _LightDirection_MaskOn2 != 0.0 || _Add_Antipodean_Rim2Light != 0.0;
#endif
#if defined(POI_RIM_1_LILTOON)
	readsLight = readsLight || _Rim2DirStrength != 0.0 || _Rim2ShadowMask != 0.0;
#endif
	if (!readsLight)
	{
		return;
	}
	PoiLight unlitLight = poiGlowUnlitLight(poiLight, poiMesh, poiCam);
	PoiFragData fragLit = poiFragData;
	PoiFragData fragUnlit = poiFragData;
	fragLit.emission = vec3(0.0);
	fragUnlit.emission = vec3(0.0);
	fragLit.finalColor = vec3(0.0);
	fragUnlit.finalColor = vec3(0.0);
	PoiMods modsLit = poiMods;
	PoiMods modsUnlit = poiMods;
	bool usesLight = false;

#if defined(POI_RIM_1_POIYOMI)
	if (_Rim2ShadowToggle != 0.0)
	{
		usesLight = true;
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
		poiApplyPoiyomiRim(fragLit, poiMesh, poiCam, poiLight, modsLit, _Is_NormalMapToRim2Light, _Rim2LightingInvert, _Rim2Power, _Rim2Linear, _Rim2Strength, _Rim2ShadowWidth, _Rim2ShadowToggle, _Rim2Width, _Rim2BlendStrength, rim2Mask, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, rim2Color, _Rim2LightColor, _Rim2LightColorThemeIndex, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2Blur, _Rim2ShadowMaskRampType, _Rim2ShadowMaskInvert, _Rim2ShadowMaskStrength, _Rim2ShadowAlpha.xy, _Rim2ApplyGlobalMaskIndex, _Rim2ApplyGlobalMaskBlendType, _Rim2BaseColorMix, _Rim2Brightness, _RimPoi2BlendMode, rim2Bias, _Rim2BiasIntensity, _Rim2ApplyAlpha, _Rim2ApplyAlphaBlend);
		poiApplyPoiyomiRim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, _Is_NormalMapToRim2Light, _Rim2LightingInvert, _Rim2Power, _Rim2Linear, _Rim2Strength, _Rim2ShadowWidth, _Rim2ShadowToggle, _Rim2Width, _Rim2BlendStrength, rim2Mask, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, rim2Color, _Rim2LightColor, _Rim2LightColorThemeIndex, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2Blur, _Rim2ShadowMaskRampType, _Rim2ShadowMaskInvert, _Rim2ShadowMaskStrength, _Rim2ShadowAlpha.xy, _Rim2ApplyGlobalMaskIndex, _Rim2ApplyGlobalMaskBlendType, _Rim2BaseColorMix, _Rim2Brightness, _RimPoi2BlendMode, rim2Bias, _Rim2BiasIntensity, _Rim2ApplyAlpha, _Rim2ApplyAlphaBlend);
	}
#endif

#if defined(POI_RIM_1_UTS2)
	if (_Is_LightColor_Rim2Light != 0.0 || _LightDirection_MaskOn2 != 0.0 || _Add_Antipodean_Rim2Light != 0.0)
	{
		usesLight = true;
#if defined(PROP_SET_RIM2LIGHTMASK)
		float Set_Rim2LightMask_var = poiTexPan(_Set_Rim2LightMask, poiUV(poiMesh.uv[int(_Set_Rim2LightMaskUV)], _Set_Rim2LightMask_ST), _Set_Rim2LightMaskPan)[int(_Set_Rim2LightMaskChannel)];
#else
		float Set_Rim2LightMask_var = 1.0;
#endif
		poiApplyUTS2Rim(fragLit, poiMesh, poiCam, poiLight, modsLit, Set_Rim2LightMask_var, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2LightColor, _Rim2LightColorThemeIndex, _Is_LightColor_Rim2Light, _Is_NormalMapToRim2Light, _Rim2Light_Power, _Rim2Light_InsideMask, _Rim2Light_FeatherOff, _LightDirection_MaskOn2, _Tweak_LightDirection_MaskLevel2, _Add_Antipodean_Rim2Light, _Ap_Rim2LightColor, _Rim2ApColorThemeIndex, _Is_LightColor_Ap_Rim2Light, _Ap_Rim2Light_Power, _Ap_Rim2Light_FeatherOff, _Tweak_Rim2LightMaskLevel, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed);
		poiApplyUTS2Rim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, Set_Rim2LightMask_var, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2LightColor, _Rim2LightColorThemeIndex, _Is_LightColor_Rim2Light, _Is_NormalMapToRim2Light, _Rim2Light_Power, _Rim2Light_InsideMask, _Rim2Light_FeatherOff, _LightDirection_MaskOn2, _Tweak_LightDirection_MaskLevel2, _Add_Antipodean_Rim2Light, _Ap_Rim2LightColor, _Rim2ApColorThemeIndex, _Is_LightColor_Ap_Rim2Light, _Ap_Rim2Light_Power, _Ap_Rim2Light_FeatherOff, _Tweak_Rim2LightMaskLevel, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed);
	}
#endif

#if defined(POI_RIM_1_LILTOON)
	if (_Rim2DirStrength != 0.0 || _Rim2ShadowMask != 0.0)
	{
		usesLight = true;
#if defined(PROP_RIM2COLORTEX)
		vec4 rim2ColorTex = poiTexPan(_Rim2ColorTex, poiUV(poiMesh.uv[int(_Rim2ColorTexUV)], _Rim2ColorTex_ST), _Rim2ColorTexPan);
#else
		vec4 rim2ColorTex = vec4(1.0);
#endif
		poiApplyLiltoonRim(fragLit, poiMesh, poiCam, poiLight, modsLit, _Rim2Color, _Rim2IndirColor, rim2ColorTex, _Rim2MainStrength, _Rim2NormalStrength, _Rim2DirRange, _Rim2IndirRange, _Rim2FresnelPower, _Rim2BackfaceMask, _Rim2DirStrength, _Rim2Border, _Rim2Blur, _Rim2IndirBorder, _Rim2IndirBlur, _Rim2ShadowMask, _Rim2EnableLighting, _Rim2VRParallaxStrength, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2BlendMode, _Rim2MaskOnlyMask, _Rim2LightColorThemeIndex);
		poiApplyLiltoonRim(fragUnlit, poiMesh, poiCam, unlitLight, modsUnlit, _Rim2Color, _Rim2IndirColor, rim2ColorTex, _Rim2MainStrength, _Rim2NormalStrength, _Rim2DirRange, _Rim2IndirRange, _Rim2FresnelPower, _Rim2BackfaceMask, _Rim2DirStrength, _Rim2Border, _Rim2Blur, _Rim2IndirBorder, _Rim2IndirBlur, _Rim2ShadowMask, _Rim2EnableLighting, _Rim2VRParallaxStrength, _Rim2GlobalMask, _Rim2GlobalMaskBlendType, _Rim2HueShiftEnabled, _Rim2HueShift, _Rim2HueShiftColorSpace, _Rim2HueSelectOrShift, _Rim2HueShiftSpeed, _Rim2BlendMode, _Rim2MaskOnlyMask, _Rim2LightColorThemeIndex);
	}
#endif

	if (usesLight)
	{
		poiGlowAddLightShare(poiLight, fragLit.baseColor - fragUnlit.baseColor, (fragLit.emission - fragUnlit.emission) * poiMods.globalEmission * _PPEmissionMultiplier + (fragLit.finalColor - fragUnlit.finalColor));
	}
}
#endif

// Rim masks that read the light (shadow masks, light direction, light color) per light. The base color
// FRAG hands over already carries the fragment's unlit rims; it stands in for the color before them.
#if defined(POI_RIM_READS_LIGHT)
#define POI_HAS_LIGHT_HOOK_RIM
#endif
void applyRimLightingLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
#if defined(POI_RIM_0)
	poiRimLightingLight0(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
#if defined(POI_RIM_1)
	poiRimLightingLight1(poiLight, poiFragData, poiMesh, poiCam, poiMods);
#endif
}
#endif

#if defined(POI_DEPTH_RIM)
const vec2 depthRimSampleOffsets4[4] = vec2[4](
	vec2(0.0, 1.0), vec2(0.0, -1.0), vec2(-1.0, 0.0), vec2(1.0, 0.0)
);
const vec2 depthRimSampleOffsets8[8] = vec2[8](
	vec2(-1.0, 1.0), vec2(0.0, 1.0), vec2(1.0, 1.0),
	vec2(-1.0, 0.0), vec2(1.0, 0.0),
	vec2(-1.0, -1.0), vec2(0.0, -1.0), vec2(1.0, -1.0)
);

// ApplyDepthRimLighting's light-free part: the depth edge, normal facing, mask and distance fade. Negative
// where Poiyomi returns early.
float poiDepthRimBase(in PoiMesh poiMesh, in PoiCam poiCam, out vec2 N_View)
{
	N_View = vec2(0.0, 1.0);
	if (!poiDepthTextureExists()) return -1.0;

	vec2 screenPos = vec2(poiCam.fragCoord.x / poiCam.screenSize.x, 1.0 - poiCam.fragCoord.y / poiCam.screenSize.y);
	float z = poiSampleScreenDepth(screenPos);

	if (z == 0.0) return -1.0;

	float linearDepth = poiLinearEyeDepth(poiCam, z);
	float distanceToVert = poiCam.distanceToVert;

	float distanceFade = 1.0;
	if (_DepthRimMaxDistance > 0.0)
	{
		distanceFade = smoothstep(_DepthRimMaxDistance, _DepthRimMinDistance + 0.001, distanceToVert);
		if (distanceFade <= 0.0) return -1.0;
	}

	float cameraClipScale = _DepthRimCameraClip > 0.0 ? clamp(distanceToVert / _DepthRimCameraClip, 0.0, 1.0) : 1.0;
	float scale = WorldToScreenScale(poiCam, _DepthRimWidth * 0.1, distanceToVert) * cameraClipScale;
	vec2 aspectCorrect = GetScreenAspectCorrection(poiCam);

	vec3 viewNorm = normalize(mat3(poiCam.viewMatrix) * poiMesh.normals[int(_DepthRimNormalToUse)]);
	float normalFacingRaw = clamp(1.0 - abs(viewNorm.z), 0.0, 1.0);
	float normalFacing = _DepthRimBinary != 0.0 ? 1.0 : clamp(1.8 - abs(normalFacingRaw - 0.5) * 3.6, 0.0, 1.0);
	vec2 viewNormXY = viewNorm.xy;
	N_View = length(viewNormXY) > 1e-6 ? normalize(viewNormXY) : vec2(0.0, 1.0);

	float rim = 0.0;
	float depthDiff = 0.0;
	float depthThreshold = _DepthRimDepthThreshold / max(cameraClipScale, 0.001);
	float depthThresholdLow = depthThreshold * _DepthRimSharpness;

	// Large depth value to use when sample hits skybox (ensures rim is detected at silhouette edges)
	float skyboxDepth = linearDepth + depthThreshold * 2.0;

	switch (int(_DepthRimType))
	{
		case 0:
		{
			vec2 ssUV1 = screenPos + N_View * aspectCorrect * scale;
			vec2 ssUV2 = screenPos - N_View * aspectCorrect * scale;
			float sampleZ1 = poiSampleScreenDepth(ssUV1);
			float sampleZ2 = poiSampleScreenDepth(ssUV2);
			float sampleDepth1 = sampleZ1 < 0.0001 ? skyboxDepth : poiLinearEyeDepth(poiCam, sampleZ1);
			float sampleDepth2 = sampleZ2 < 0.0001 ? skyboxDepth : poiLinearEyeDepth(poiCam, sampleZ2);
			depthDiff = max(sampleDepth1 - linearDepth, sampleDepth2 - linearDepth);

			rim = _DepthRimBinary != 0.0 ? step(depthThreshold, depthDiff) : smoothstep(depthThresholdLow, depthThreshold, depthDiff);
			rim *= normalFacing;
			break;
		}
		case 1:
		{
			for (int i = 0; i < 4; i++)
			{
				vec2 ssUV1 = screenPos + depthRimSampleOffsets4[i] * aspectCorrect * scale;
				float sampleZ = poiSampleScreenDepth(ssUV1);
				float sampleDepth = sampleZ < 0.0001 ? skyboxDepth : poiLinearEyeDepth(poiCam, sampleZ);
				depthDiff = max(depthDiff, sampleDepth - linearDepth);
			}
			rim = _DepthRimBinary != 0.0 ? step(depthThreshold, depthDiff) : smoothstep(depthThresholdLow, depthThreshold, depthDiff);
			rim *= normalFacing;
			break;
		}
		case 2:
		{
			for (int i = 0; i < 8; i++)
			{
				vec2 ssUV1 = screenPos + depthRimSampleOffsets8[i] * aspectCorrect * scale;
				float sampleZ = poiSampleScreenDepth(ssUV1);
				float sampleDepth = sampleZ < 0.0001 ? skyboxDepth : poiLinearEyeDepth(poiCam, sampleZ);
				depthDiff = max(depthDiff, sampleDepth - linearDepth);
			}
			rim = _DepthRimBinary != 0.0 ? step(depthThreshold, depthDiff) : smoothstep(depthThresholdLow, depthThreshold, depthDiff);
			rim *= normalFacing;
			break;
		}
	}

#if defined(PROP_DEPTHRIMMASK)
	float mask = poiTexPan(_DepthRimMask, poiUV(poiMesh.uv[int(_DepthRimMaskUV)], _DepthRimMask_ST), _DepthRimMaskPan)[int(_DepthRimMaskChannel)];
#else
	float mask = 1.0;
#endif
	if (_DepthRimBinary >= 0.5)
	{
		rim *= step(0.5, mask) * step(0.5, distanceFade);
	}
	else
	{
		rim *= mask * distanceFade;
	}
	return rim;
}

// The light-dependent rest. Ignore Lighting divides the rim color by finalLighting so the base color comes
// out unlit once lit; here that share is kept apart in unlitColor (exact for one blend at a time), and the
// additive lighting and emission use the rim color undivided.
void poiDepthRimApply(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiLight poiLight, in PoiMods poiMods, float rim, vec2 N_View, out vec3 unlitColor)
{
	vec3 viewLight = normalize(mat3(poiCam.viewMatrix) * poiLight.direction);
	vec2 viewLightXY = viewLight.xy;
	vec2 L_View = length(viewLightXY) > 1e-6 ? normalize(viewLightXY) : vec2(0.0, 1.0);

	// Light Direction
	float lightDirFactor = _DepthRimLightDirMethod == 0.0
		? clamp(mix(poiLight.vertexNDotL, poiLight.nDotL, _DepthRimNormalToUse), 0.0, 1.0)
		: clamp(dot(L_View, N_View), 0.0, 1.0);
	rim *= mix(1.0, lightDirFactor, _DepthRimHideInShadow);
	rim *= mix(1.0, poiLight.lightMap, _DepthRimShadowMask);
	rim *= mix(1.0, dot(poiLight.rampedLightMap, vec3(0.299, 0.587, 0.114)), _DepthRimMixRampedLightMap);

	vec3 rimColor = poiThemeColor(poiMods, _DepthRimColor.rgb, _DepthRimColorThemeIndex).rgb * mix(vec3(1.0), poiLight.directColor, _DepthRimMixLightColor) * mix(vec3(1.0), poiFragData.baseColor, _DepthRimMixBaseColor) * _DepthRimBrightness;
	vec3 rimColorLit = rimColor * (1.0 - _DepthRimIgnoreLighting);
	vec3 rimColorUnlit = rimColor * _DepthRimIgnoreLighting;

#if defined(POI_PASS_BASE) || defined(POI_PASS_OUTLINE)
	poiLight.finalLightAdd += rim * rimColor * _DepthRimAdditiveLighting;
#endif
	poiFragData.emission += rim * rimColor * _DepthRimEmission;

	unlitColor = vec3(0.0);
	float amount = rim * _DepthRimReplace;
	poiFragData.baseColor = mix(poiFragData.baseColor, rimColorLit, amount);
	unlitColor = mix(unlitColor, rimColorUnlit, amount);

	amount = rim * _DepthRimAdd;
	poiFragData.baseColor += amount * rimColorLit;
	unlitColor += amount * rimColorUnlit;

	amount = rim * _DepthRimScreen;
	unlitColor = unlitColor * (1.0 - amount * rimColorLit) + amount * rimColorUnlit * (1.0 - poiFragData.baseColor);
	poiFragData.baseColor = mix(poiFragData.baseColor, 1.0 - (1.0 - poiFragData.baseColor) * (1.0 - rimColorLit), amount);

	amount = rim * _DepthRimMultiply;
	unlitColor = unlitColor * mix(vec3(1.0), rimColorLit, amount) + amount * poiFragData.baseColor * rimColorUnlit;
	poiFragData.baseColor *= mix(vec3(1.0), rimColorLit, amount);
}

// The unlit share goes to poiLight.finalLightAdd, which the fragment adds unlit (finalColor += finalLightAdd).
void ApplyDepthRimLighting(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiLight poiLight, in PoiMods poiMods)
{
	vec2 N_View = vec2(0.0, 1.0);
	float rim = poiDepthRimBase(poiMesh, poiCam, N_View);
	if (rim < 0.0) return;

	PoiLight unlitLight = poiGlowUnlitLight(poiLight, poiMesh, poiCam);
	unlitLight.finalLightAdd = vec3(0.0);
	vec3 unlitColor = vec3(0.0);
	poiDepthRimApply(poiFragData, poiMesh, poiCam, unlitLight, poiMods, rim, N_View, unlitColor);
	poiLight.finalLightAdd += unlitLight.finalLightAdd + unlitColor;
}

// Directional lights take Poiyomi's base pass share (base color, emission, additive lighting, unlit
// color); omni and spot lights its add pass share, where the base color change is lit and the Ignore
// Lighting color, divided by that light's lighting, reaches the screen whole.
#define POI_HAS_LIGHT_HOOK_DEPTH_RIM
void applyDepthRimLightingLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	bool usesLight = _DepthRimHideInShadow != 0.0 || _DepthRimShadowMask != 0.0 || _DepthRimMixRampedLightMap != 0.0 || _DepthRimMixLightColor != 0.0;
	if (!usesLight && (poiLight.isDirectional || _DepthRimIgnoreLighting == 0.0))
	{
		return;
	}

	vec2 N_View = vec2(0.0, 1.0);
	float rim = poiDepthRimBase(poiMesh, poiCam, N_View);
	if (rim <= 0.0) return;

	PoiFragData fragLit = poiFragData;
	PoiFragData fragUnlit = poiFragData;
	fragLit.emission = vec3(0.0);
	fragUnlit.emission = vec3(0.0);
	PoiLight lightLit = poiLight;
	lightLit.finalLightAdd = vec3(0.0);
	PoiLight lightUnlit = poiGlowUnlitLight(poiLight, poiMesh, poiCam);
	lightUnlit.finalLightAdd = vec3(0.0);
	vec3 unlitColorLit = vec3(0.0);
	vec3 unlitColorUnlit = vec3(0.0);
	poiDepthRimApply(fragLit, poiMesh, poiCam, lightLit, poiMods, rim, N_View, unlitColorLit);
	poiDepthRimApply(fragUnlit, poiMesh, poiCam, lightUnlit, poiMods, rim, N_View, unlitColorUnlit);

	if (poiLight.isDirectional)
	{
		poiGlowAddLightShare(poiLight, fragLit.baseColor - fragUnlit.baseColor, (fragLit.emission - fragUnlit.emission) * poiMods.globalEmission * _PPEmissionMultiplier + (lightLit.finalLightAdd - lightUnlit.finalLightAdd) + (unlitColorLit - unlitColorUnlit));
	}
	else
	{
		poiGlowAddLightShare(poiLight, fragLit.baseColor - fragUnlit.baseColor, vec3(0.0));
		poiLight.finalLightAdd += unlitColorLit * min(poiLight.finalLighting / 0.0001, vec3(1.0));
	}
}
#endif
