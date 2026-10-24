// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Main color and texture, color adjust, alpha options, dithering and alpha to coverage, video effects,
// main, second and detail normals, global masks, the UV set (UV settings, polar, panosphere,
// world, local, view normal, screen space, distortion), UDIM discard, parallax, black light masking,
// skin, vertex color, back face and the RGB mask.
//
// Handedness: imported content arrives with X negated against the source's left-handed world and object
// spaces, so the mappings from a world or object coordinate to a UV (world UV, local UV, panosphere,
// distortion flow) and authored object-space directions read X negated, and the view-normal UV builds
// its right axis for a right-handed world. Distances and dot products need nothing.
//
// Derivatives: calculatePolarCoordinate and calculatePanosphereUV pick their seam-free branch with
// fwidth, as Poiyomi does. light() may call them: there the pick is only defined where the whole
// pixel quad runs that light (directional lights always, omni and spot inside one cluster), the same
// condition the implicit-LOD fetches in light() already live under. -xlinka

vec3 poiLeftHanded(vec3 v)
{
	return vec3(-v.x, v.y, v.z);
}

// Clamped reads where the source samples with a clamping sampler but the material sampler repeats:
// keep the coordinate on the edge texel centres. A macro: sampler parameters only take one filter and
// repeat setting per shader.
#define poiMainClampUV(tex, uv) clamp((uv), 0.5 * poiTexelSize(tex).xy, 1.0 - 0.5 * poiTexelSize(tex).xy)

#if defined(POI_UDIMDISCARD)
float shouldPerformUDIMDiscard(vec2 udim, vec4 UDIMDiscardRows[4])
{
	float shouldDiscard = 0.0;
	vec4 xMask = vec4(float(udim.x >= 0.0 && udim.x < 1.0),
	float(udim.x >= 1.0 && udim.x < 2.0),
	float(udim.x >= 2.0 && udim.x < 3.0),
	float(udim.x >= 3.0 && udim.x < 4.0));

	shouldDiscard += float(udim.y >= 0.0 && udim.y < 1.0) * dot(UDIMDiscardRows[0], xMask);
	shouldDiscard += float(udim.y >= 1.0 && udim.y < 2.0) * dot(UDIMDiscardRows[1], xMask);
	shouldDiscard += float(udim.y >= 2.0 && udim.y < 3.0) * dot(UDIMDiscardRows[2], xMask);
	shouldDiscard += float(udim.y >= 3.0 && udim.y < 4.0) * dot(UDIMDiscardRows[3], xMask);

	// never discard outside 4x4 grid in pos coords
	shouldDiscard *= float(udim.y >= 0.0 || udim.y < 4.0 || udim.x >= 0.0 || udim.x < 4.0);

	// Use a threshold so that there's some room for animations to be close to 0, but not exactly 0
	const float threshold = 0.001;
	return threshold - shouldDiscard;
}
#endif

void applyAlphaOptions(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiMods poiMods)
{
	poiFragData.alpha = clamp(poiFragData.alpha + _AlphaMod, 0.0, 1.0);

	if (_AlphaGlobalMask > 0.0)
	{
		poiFragData.alpha = maskBlend(poiFragData.alpha, poiMods.globalMask[int(_AlphaGlobalMask) - 1], _AlphaGlobalMaskBlendType);
	}

	if (_AlphaDistanceFade >= 0.5)
	{
		vec3 position = _AlphaDistanceFadeType != 0.0 ? poiMesh.worldPos : poiMesh.objectPosition;
		float distanceFadeMultiplier = mix(_AlphaDistanceFadeMinAlpha, _AlphaDistanceFadeMaxAlpha, smoothstep(_AlphaDistanceFadeMin, _AlphaDistanceFadeMax, distance(position, poiCam.worldPos)));
		if (_AlphaDistanceFadeGlobalMask > 0.0)
		{
			distanceFadeMultiplier = mix(1.0, distanceFadeMultiplier, poiMods.globalMask[int(_AlphaDistanceFadeGlobalMask) - 1]);
		}
		poiFragData.alpha *= distanceFadeMultiplier;
	}

	if (_AlphaFresnel >= 0.5)
	{
		float holoRim = clamp(1.0 - smoothstep(min(_AlphaFresnelSharpness, _AlphaFresnelWidth), _AlphaFresnelWidth, poiCam.vDotNCentered), 0.0, 1.0);
		holoRim = abs(mix(1.0, holoRim, _AlphaFresnelAlpha));
		holoRim = _AlphaFresnelInvert != 0.0 ? 1.0 - holoRim : holoRim;
		if (_AlphaFresnelGlobalMask > 0.0)
		{
			holoRim = mix(1.0, holoRim, poiMods.globalMask[int(_AlphaFresnelGlobalMask) - 1]);
		}
		poiFragData.alpha *= holoRim;
	}

	if (_AlphaAngular >= 0.5)
	{
		float cameraAngleMin = _CameraAngleMin / 180.0;
		float cameraAngleMax = _CameraAngleMax / 180.0;
		float modelAngleMin = _ModelAngleMin / 180.0;
		float modelAngleMax = _ModelAngleMax / 180.0;
		vec3 pos = _AngleCompareTo == 0.0 ? poiMesh.objectPosition : poiMesh.worldPos;
		vec3 cameraToModelDirection = normalize(pos - poiCam.worldPos);
		vec3 modelForwardDirection = normalize(mat3(poiMesh.modelMatrix) * poiLeftHanded(normalize(_AngleForwardDirection.rgb)));
		float cameraLookAtModel = remapClamped(cameraAngleMax, cameraAngleMin, 0.5 * dot(cameraToModelDirection, poiCam.forwardDir) + 0.5, 0.0, 1.0);
		float modelLookAtCamera = remapClamped(modelAngleMax, modelAngleMin, 0.5 * dot(-cameraToModelDirection, modelForwardDirection) + 0.5, 0.0, 1.0);
		float angularAlphaMod = 1.0;
		if (_AngleType == 0.0)
		{
			angularAlphaMod = max(cameraLookAtModel, _AngleMinAlpha);
		}
		else if (_AngleType == 1.0)
		{
			angularAlphaMod = max(modelLookAtCamera, _AngleMinAlpha);
		}
		else if (_AngleType == 2.0)
		{
			angularAlphaMod = max(cameraLookAtModel * modelLookAtCamera, _AngleMinAlpha);
		}
		if (_AlphaAngularGlobalMask > 0.0)
		{
			angularAlphaMod = mix(1.0, angularAlphaMod, poiMods.globalMask[int(_AlphaAngularGlobalMask) - 1]);
		}
		poiFragData.alpha *= angularAlphaMod;
	}
}

// Premultiplied by 1/64
const float POI_DITHER_8X8[64] = float[64](
	0.015625, 0.765625, 0.203125, 0.953125, 0.06250, 0.81250, 0.25000, 1.00000,
	0.515625, 0.265625, 0.703125, 0.453125, 0.56250, 0.31250, 0.75000, 0.50000,
	0.140625, 0.890625, 0.078125, 0.828125, 0.18750, 0.93750, 0.12500, 0.87500,
	0.640625, 0.390625, 0.578125, 0.328125, 0.68750, 0.43750, 0.62500, 0.37500,
	0.046875, 0.796875, 0.234375, 0.984375, 0.03125, 0.78125, 0.21875, 0.96875,
	0.546875, 0.296875, 0.734375, 0.484375, 0.53125, 0.28125, 0.71875, 0.46875,
	0.171875, 0.921875, 0.109375, 0.859375, 0.15625, 0.90625, 0.09375, 0.84375,
	0.671875, 0.421875, 0.609375, 0.359375, 0.65625, 0.40625, 0.59375, 0.34375
);

float Dither8x8Bayer(int x, int y)
{
	int r = y * 8 + x;
	return POI_DITHER_8X8[r];
}

float calcDither(vec2 grabPos)
{
	return Dither8x8Bayer(int(mod(grabPos.x, 8.0)), int(mod(grabPos.y, 8.0)));
}

void applyDithering(inout PoiFragData poiFragData, in PoiCam poiCam)
{
	if (_AlphaDithering >= 0.5)
	{
		float dither = calcDither(poiCam.posScreenPixels) - _AlphaDitherBias;
		poiFragData.alpha = clamp(poiFragData.alpha - (dither * (1.0 - poiFragData.alpha) * _AlphaDitherGradient), 0.0, 1.0);
	}
}

// Fragment only: derivatives.
void ApplyAlphaToCoverage(inout PoiFragData poiFragData, in PoiMesh poiMesh)
{
	// Force Model Opacity to 1 if desired
	if (_Mode == 1.0)
	{
		if (_AlphaSharpenedA2C >= 0.5 && _AlphaToCoverage >= 0.5)
		{
			// rescale alpha by mip level
			poiFragData.alpha *= 1.0 + max(0.0, CalcMipLevel(poiMesh.uv[0] * poiTexelSize(_MainTex).zw)) * _AlphaMipScale;
			// rescale alpha by partial derivative
			poiFragData.alpha = (poiFragData.alpha - _Cutoff) / max(fwidth(poiFragData.alpha), 0.0001) + _Cutoff;
			poiFragData.alpha = clamp(poiFragData.alpha, 0.0, 1.0);
		}
	}
}

#if defined(POI_BENTNORMALMAP)
// Jimenez 2016, "Practical Realtime Strategies for Accurate Indirect Occlusion"
// Cone intersection for specular occlusion using bent normal
float PoiSpecularOcclusionBentNormal(vec3 reflectionDir, vec3 bentNormal, float occlusion, float roughness)
{
	roughness = max(roughness, 0.01);

	// Unoccluded cone angle from scalar AO
	float cosUnoccludedAngle = sqrt(1.0 - occlusion);
	float unoccludedAngle = acos(cosUnoccludedAngle);

	// Specular cone angle from roughness
	float cosSpecularAngle = exp2(-3.32192809489 * (roughness * roughness));
	float specularAngle = acos(cosSpecularAngle);

	// Angle between bent normal and reflection direction
	float angleBentToReflection = acos(clamp(dot(bentNormal, reflectionDir), 0.0, 1.0));

	// Cone intersection (smoothstep approximation)
	float angleSum = unoccludedAngle + specularAngle;
	float angleDiff = abs(unoccludedAngle - specularAngle);
	float intersection = 1.0 - smoothstep(0.0, 1.0, clamp((angleBentToReflection - angleDiff) / max(angleSum - angleDiff, 0.0001), 0.0, 1.0));

	return min(intersection, occlusion);
}
#endif

#if defined(POI_VIDEO_EFFECTS)
// Shared Screen / Video Effects Core Helpers
struct PoiScreenEffect
{
	int type;
	float backlight;
	float contrast;
	float saturation;
	vec2 resolution;
	float crtRefreshRate;
	float crtPixelEnergizedTime;
};

vec3 poiScreenBacklight(vec3 tex, float strength)
{
	return max(vec3(strength), tex.rgb);
}

void poiScreenContrast(inout vec3 pixel, float contrast)
{
	pixel = LinearToGammaSpace(pixel);
	pixel = clamp(mix(vec3(0.5), pixel, contrast + 1.0), 0.0, 1.0);
	pixel = GammaToLinearSpace(pixel);
}

void poiScreenSaturation(inout vec3 pixel, float saturation)
{
	pixel = mix(pixel.rgb, vec3(dot(pixel.rgb, vec3(0.3, 0.59, 0.11))), -saturation);
}

void poiScreenColorGrade(inout vec3 pixel, in PoiScreenEffect se)
{
	poiScreenSaturation(pixel, se.saturation);
	poiScreenContrast(pixel, se.contrast);
}

vec3 poiScreenViewAngleTN(vec3 tex, in PoiCam poiCam, in PoiMesh poiMesh)
{
	vec3 reflectionVector = normalize(reflect(poiCam.eyeViewDir.rgb, poiMesh.normals[1].rgb));
	float upwardShift = dot(reflectionVector, poiMesh.binormal[0]);
	float sideShift = dot(reflectionVector, poiMesh.tangent[0]);
	// pow(x, 3) with a literal exponent compiles to x * x * x in the source, so negatives keep their sign.
	sideShift *= sideShift * sideShift * sideShift;
	tex = LinearToGammaSpace(tex);
	tex = clamp(mix(vec3(0.5), tex, upwardShift + 1.0), 0.0, 1.0);
	tex = GammaToLinearSpace(tex);
	tex = mix(tex, tex.gbr, sideShift);
	return tex;
}

float poiScreenCRTBrightness(vec2 uv, in PoiScreenEffect se)
{
	float totalPixels = se.resolution.x * se.resolution.y;
	vec2 uvPixel = vec2((floor((1.0 - uv.y) * se.resolution.y)) / se.resolution.y, (floor(uv.x * se.resolution.x)) / se.resolution.x);
	float currentPixelNumber = se.resolution.x * (se.resolution.y * uvPixel.x) + se.resolution.y * uvPixel.y;
	float electronBeamAlpha = fract(POI_TIME.y * se.crtRefreshRate);
	float electronBeamPixelNumber = totalPixels * electronBeamAlpha;

	float dist = (electronBeamPixelNumber >= currentPixelNumber) ? electronBeamPixelNumber - currentPixelNumber : electronBeamPixelNumber + (totalPixels - currentPixelNumber);

	float timeSince = dist / totalPixels;
	return clamp(se.crtPixelEnergizedTime - timeSince, 0.0, 1.0);
}

// Shared Screen Dispatcher
void poiApplyScreenEffect(inout vec4 color, in PoiScreenEffect se, vec3 pixels, vec2 uv, in PoiCam poiCam, in PoiMesh poiMesh)
{
	vec3 rgb = color.rgb;
	if (se.type == 0) // LCD
	{
		rgb = poiScreenBacklight(rgb, se.backlight * 0.01);
		poiScreenColorGrade(rgb, se);
		rgb = rgb * pixels * se.backlight;
	}
	else if (se.type == 1) // TN
	{
		rgb = poiScreenBacklight(rgb, se.backlight * 0.01);
		rgb = poiScreenViewAngleTN(rgb, poiCam, poiMesh);
		poiScreenColorGrade(rgb, se);
		rgb = rgb * pixels * se.backlight;
	}
	else if (se.type == 2) // CRT
	{
		poiScreenColorGrade(rgb, se);
		rgb = rgb * pixels * poiScreenCRTBrightness(uv, se) * se.backlight;
	}
	else if (se.type == 3) // OLED
	{
		poiScreenColorGrade(rgb, se);
		rgb = rgb * pixels * se.backlight;
	}
	else if (se.type == 4) // Gameboy
	{
		poiScreenColorGrade(rgb, se);
		float brightness = LinearRgbToLuminance(LinearToGammaSpace(rgb));
		const vec3 darkGreen = vec3(0.00392156863, 0.0392156863, 0.00392156863);
		const vec3 lightGreen = vec3(0.333333333, 0.5, 0.00392156863);
		rgb = mix(darkGreen, lightGreen, brightness);

#if defined(PROP_VIDEOGAMEBOYRAMP)
		vec4 rampTexelSize = poiTexelSize(_VideoGameboyRamp);
		if (max(rampTexelSize.z, rampTexelSize.w) > 16.0)
		{
			rgb = poiTexLod(_VideoGameboyRamp, vec2(brightness), 0.0).rgb;
		}
#endif
	}
	else if (se.type == 5) // Projector
	{
		poiScreenColorGrade(rgb, se);
		vec3 projectorColor = rgb * se.backlight;
		rgb.r = clamp(projectorColor.r, rgb.r, 1000.0);
		rgb.g = clamp(projectorColor.g, rgb.g, 1000.0);
		rgb.b = clamp(projectorColor.b, rgb.b, 1000.0);
	}
	color.rgb = rgb;
}

// Fragment only: derivatives. The pixel grid belongs to the screen, not to the main texture's own UV,
// so a view dependent main UV reads as content shown on the screen instead of swimming under a lattice
// welded to the mesh. Both selectors are uniforms, so the branch is uniform for the derivatives inside.
void applyVideoEffectsMainTex(inout vec4 mainTexture, in PoiMesh poiMesh)
{
	if (_VideoPixelateToResolution >= 0.5)
	{
		vec2 rawUV = poiMesh.uv[int(_MainTexUV)];

#if defined(PROP_VIDEOPIXELTEXTURE)
		int gridUV = int(_VideoPixelTextureUV);
#else
		// Property stripped; 0 is its default, so the grid stays on UV0.
		int gridUV = 0;
#endif

		vec2 snappedUV = vec2(0.0);
		vec2 cellFootprint = vec2(0.0);
		if (int(_MainTexUV) == gridUV)
		{
			snappedUV = poiSnapUVToGrid(rawUV, _VideoResolution);
			cellFootprint = 1.0 / _VideoResolution;
		}
		else
		{
			snappedUV = poiSnapUVToForeignGrid(rawUV, poiMesh.uv[gridUV], _VideoResolution, cellFootprint);
		}

		vec2 uvs = poiUV(snappedUV, _MainTex_ST) + POI_TIME.x * _MainTexPan;

		// One video pixel, and the un-snapped UV, both in _MainTex space.
		vec2 videoPixel = abs(_MainTex_ST.xy) * cellFootprint;
		vec2 continuousUV = poiUV(rawUV, _MainTex_ST) + POI_TIME.x * _MainTexPan;

		vec2 dx = vec2(0.0);
		vec2 dy = vec2(0.0);
		poiSnapUVGradients(continuousUV, videoPixel, dx, dy);

		mainTexture = poiTexGrad(_MainTex, uvs, dx, dy);
	}
}

void applyVideoEffects(inout PoiFragData poiFragData, in PoiCam poiCam, in PoiMesh poiMesh, in PoiMods poiMods)
{
#if defined(PROP_VIDEOPIXELTEXTURE)
	vec3 pixels = poiTex(_VideoPixelTexture, poiUV(poiMesh.uv[int(_VideoPixelTextureUV)], _VideoPixelTexture_ST) * _VideoResolution).rgb;
#else
	vec3 pixels = vec3(1.0);
#endif
	vec2 uvs = poiMesh.uv[int(_MainTexUV)];
	if (_VideoPixelateToResolution >= 0.5)
	{
		uvs = poiSnapUVToGrid(uvs, _VideoResolution);
	}
	uvs = poiUV(uvs, _MainTex_ST) + POI_TIME.x * _MainTexPan;
	vec4 modifiedVideoTexture = vec4(0.0);
	modifiedVideoTexture.rgb = poiFragData.baseColor;
	modifiedVideoTexture.a = poiFragData.alpha;

	PoiScreenEffect se;
	se.type = int(_VideoType);
	se.backlight = _VideoBacklight;
	se.contrast = _VideoContrast;
	se.saturation = _VideoSaturation;
	se.resolution = _VideoResolution;
	se.crtRefreshRate = _VideoCRTRefreshRate;
	se.crtPixelEnergizedTime = _VideoCRTPixelEnergizedTime;
	poiApplyScreenEffect(modifiedVideoTexture, se, pixels, uvs, poiCam, poiMesh);

#if defined(PROP_VIDEOMASKTEXTURE)
	float screenMask = poiTexPan(_VideoMaskTexture, poiUV(poiMesh.uv[int(_VideoMaskTextureUV)], _VideoMaskTexture_ST), _VideoMaskTexturePan)[int(_VideoMaskTextureChannel)];
#else
	float screenMask = 1.0;
#endif

	if (_VideoGlobalMask > 0.0)
	{
		screenMask = maskBlend(screenMask, poiMods.globalMask[int(_VideoGlobalMask) - 1], _VideoGlobalMaskBlendType);
	}

	poiFragData.baseColor = mix(poiFragData.baseColor, modifiedVideoTexture.rgb, screenMask);
	if (_VideoEmissionEnabled >= 0.5)
	{
		poiFragData.emission += modifiedVideoTexture.rgb * screenMask;
	}
}
#endif

void poiApplyAlphaMask(inout PoiFragData poiFragData, in PoiMesh poiMesh)
{
	if (_MainAlphaMaskMode != 0.0)
	{
		vec4 alphaMaskTex = vec4(1.0);
#if defined(PROP_ALPHAMASK)
		alphaMaskTex = poiTexPan(_AlphaMask, poiUV(poiMesh.uv[int(_AlphaMaskUV)], _AlphaMask_ST), _AlphaMaskPan.xy);
#endif
		float alphaMask = poiSampleMask(alphaMaskTex, vec4(_AlphaMaskR, _AlphaMaskG, _AlphaMaskB, _AlphaMaskA), _AlphaMaskChannelBlendMode, _AlphaMaskInvert, _AlphaMaskMinMax, _AlphaMaskGamma, _AlphaMaskBlendStrength, _AlphaMaskValue);
		int alphaMaskMode = int(_MainAlphaMaskMode);
		if (alphaMaskMode == 1)
		{
			poiFragData.alpha = alphaMask;
		}
		else if (alphaMaskMode == 2)
		{
			poiFragData.alpha *= alphaMask;
		}
		else if (alphaMaskMode == 3)
		{
			poiFragData.alpha = clamp(poiFragData.alpha + alphaMask, 0.0, 1.0);
		}
		else if (alphaMaskMode == 4)
		{
			poiFragData.alpha = clamp(poiFragData.alpha - alphaMask, 0.0, 1.0);
		}
	}
}

void applyMainColor(inout PoiFragData poiFragData, inout PoiMesh poiMesh, inout PoiCam poiCam, inout PoiMods poiMods, inout vec4 mainTexture)
{
	poiFragData.baseColor = mainTexture.rgb;
	poiFragData.baseColor *= poiThemeColor(poiMods, _Color.rgb, _ColorThemeIndex);
	poiFragData.alpha = mainTexture.a * _Color.a;

#if defined(POI_COLOR_ADJUST)
#if defined(PROP_MAINCOLORADJUSTTEXTURE)
	vec4 hueShiftAlpha = poiTexPan(_MainColorAdjustTexture, poiUV(poiMesh.uv[int(_MainColorAdjustTextureUV)], _MainColorAdjustTexture_ST), _MainColorAdjustTexturePan);
#else
	vec4 hueShiftAlpha = vec4(1.0);
#endif

	if (_MainHueGlobalMask > 0.0)
	{
		hueShiftAlpha.r = maskBlend(hueShiftAlpha.r, poiMods.globalMask[int(_MainHueGlobalMask) - 1], _MainHueGlobalMaskBlendType);
	}
	if (_MainSaturationGlobalMask > 0.0)
	{
		hueShiftAlpha.b = maskBlend(hueShiftAlpha.b, poiMods.globalMask[int(_MainSaturationGlobalMask) - 1], _MainSaturationGlobalMaskBlendType);
	}
	if (_MainBrightnessGlobalMask > 0.0)
	{
		hueShiftAlpha.g = maskBlend(hueShiftAlpha.g, poiMods.globalMask[int(_MainBrightnessGlobalMask) - 1], _MainBrightnessGlobalMaskBlendType);
	}
	if (_MainGammaGlobalMask > 0.0)
	{
		hueShiftAlpha.a = maskBlend(hueShiftAlpha.a, poiMods.globalMask[int(_MainGammaGlobalMask) - 1], _MainGammaGlobalMaskBlendType);
	}

	if (_MainHueShiftToggle >= 0.5)
	{
		float shift = _MainHueShift;
		if (_MainHueShiftReplace >= 0.5)
		{
			poiFragData.baseColor = mix(poiFragData.baseColor, hueShift(poiFragData.baseColor, shift + _MainHueShiftSpeed * POI_TIME.x, _MainHueShiftColorSpace, _MainHueShiftSelectOrShift), hueShiftAlpha.r);
		}
		else
		{
			poiFragData.baseColor = hueShift(poiFragData.baseColor, fract((shift - (1.0 - hueShiftAlpha.r) + _MainHueShiftSpeed * POI_TIME.x)), _MainHueShiftColorSpace, _MainHueShiftSelectOrShift);
		}
	}

	if (_MainGradationStrength != 0.0 && _ColorGradingToggle >= 0.5)
	{
		vec3 tempColor = OpenLitLinearToSRGB(poiFragData.baseColor);
#if defined(PROP_MAINGRADATIONTEX)
		tempColor.r = poiTex(_MainGradationTex, poiMainClampUV(_MainGradationTex, vec2(tempColor.r, 0.5))).r;
		tempColor.g = poiTex(_MainGradationTex, poiMainClampUV(_MainGradationTex, vec2(tempColor.g, 0.5))).g;
		tempColor.b = poiTex(_MainGradationTex, poiMainClampUV(_MainGradationTex, vec2(tempColor.b, 0.5))).b;
#else
		tempColor = vec3(1.0, 1.0, 1.0);
#endif
		tempColor = OpenLitSRGBToLinear(tempColor);
		float gradationMask = 1.0;
		if (_MainGradationMaskChannel > 0.0)
		{
			gradationMask = hueShiftAlpha[int(_MainGradationMaskChannel) - 1];
		}
		poiFragData.baseColor = mix(poiFragData.baseColor, tempColor, _MainGradationStrength * gradationMask);
	}
	if (_MainGamma != 1.0)
	{
		vec3 gammaAdjustedColor = sign(poiFragData.baseColor) * pow(abs(poiFragData.baseColor), vec3(_MainGamma));
		poiFragData.baseColor = mix(poiFragData.baseColor, gammaAdjustedColor, hueShiftAlpha.a);
	}
	poiFragData.baseColor = mix(poiFragData.baseColor, vec3(dot(poiFragData.baseColor, vec3(0.3, 0.59, 0.11))), -(_Saturation) * hueShiftAlpha.b);
	if (_MainChromatize != 0.0)
	{
		vec3 chromaLinear = poiFragData.baseColor;
		chromaLinear = PoiApplyChromatize(chromaLinear, _MainChromatize * hueShiftAlpha.r);
		poiFragData.baseColor = clamp(chromaLinear, 0.0, 1.0);
	}
	if (_MainTintColor.a > 0.0)
	{
#if defined(PROP_MAINTINTTEXTURE)
		vec4 mainTintTexSample = poiTexPan(_MainTintTexture, poiUV(poiMesh.uv[int(_MainTintTextureUV)], _MainTintTexture_ST), _MainTintTexturePan);
#else
		vec4 mainTintTexSample = vec4(1.0);
#endif
		vec3 finalTintColor = _MainTintColor.rgb * mainTintTexSample.rgb;
		float finalTintAlpha = _MainTintColor.a * mainTintTexSample.a;

		vec3 tintInputLinear = poiFragData.baseColor;
		vec3 tintColorLinear = finalTintColor;

		// Get tint color's hue and saturation factor
		vec3 tintOklab = linear_srgb_to_oklab(tintColorLinear);
		float tintHue = atan(tintOklab.z, tintOklab.y);
		float tintMin = min(tintColorLinear.r, min(tintColorLinear.g, tintColorLinear.b));
		float tintMax = max(tintColorLinear.r, max(tintColorLinear.g, tintColorLinear.b));
		float tintSatFactor = (tintMax > 0.0001) ? 1.0 - (tintMin / tintMax) : 0.0;

		// Get input's lightness and chroma
		vec3 inputOklab = linear_srgb_to_oklab(tintInputLinear);
		float inputL = inputOklab.x;
		float inputChroma = length(inputOklab.yz);

		// Combine: input's L, tint's H, input's C scaled by tint saturation
		float newChroma = inputChroma * tintSatFactor;
		vec3 tintedOklab = vec3(inputL, newChroma * cos(tintHue), newChroma * sin(tintHue));
		vec3 tintedLinear = clamp(oklab_to_linear_srgb(tintedOklab), 0.0, 1.0);

		// Lerp by alpha
		tintInputLinear = mix(tintInputLinear, tintedLinear, finalTintAlpha);

		poiFragData.baseColor = tintInputLinear;
	}
	poiFragData.baseColor = mix(poiFragData.baseColor, poiFragData.baseColor * (_MainBrightness + 1.0), hueShiftAlpha.g);
	if (_MainColorAdjustClamp > 0.5)
	{
		poiFragData.baseColor = clamp(poiFragData.baseColor, 0.0, 1.0);
	}
#endif

	poiApplyAlphaMask(poiFragData, poiMesh);
}

void applyMainNormal(inout PoiMesh poiMesh)
{
#if defined(PROP_BUMPMAP)
	poiMesh.tangentSpaceNormal = PoiUnpackNormalScale(poiTexPanStochastic(_BumpMap, poiUV(poiMesh.uv[int(_BumpMapUV)].xy, _BumpMap_ST), _BumpMapPan, _BumpMapStochastic), _BumpScale);
#else
	poiMesh.tangentSpaceNormal = vec3(0.0, 0.0, 1.0);
#endif
}

#if defined(POI_BUMP2NDMAP)
void applyMainNormal2(inout PoiMesh poiMesh, in PoiMods poiMods)
{
#if defined(PROP_BUMP2NDMAP)
#if defined(PROP_BUMP2NDSCALEMASK)
	float bump2ndMask = poiTexPan(_Bump2ndScaleMask, poiUV(poiMesh.uv[int(_Bump2ndScaleMaskUV)].xy, _Bump2ndScaleMask_ST), _Bump2ndScaleMaskPan).r;
#else
	float bump2ndMask = 1.0;
#endif
	if (_Bump2ndScaleMaskGlobalMask > 0.0)
	{
		bump2ndMask = maskBlend(bump2ndMask, poiMods.globalMask[int(_Bump2ndScaleMaskGlobalMask) - 1], _Bump2ndScaleMaskGlobalMaskBlendType);
	}
	vec3 bump2ndNormal = PoiUnpackNormalScale(poiTexPanStochastic(_Bump2ndMap, poiUV(poiMesh.uv[int(_Bump2ndMapUV)].xy, _Bump2ndMap_ST), _Bump2ndMapPan, _Bump2ndMapStochastic), _Bump2ndScale * bump2ndMask);
	poiMesh.tangentSpaceNormal = PoiBlendNormal(bump2ndNormal, poiMesh.tangentSpaceNormal);
#endif
}
#endif

#if defined(POI_GLOBALMASK_TEXTURES)
void ApplyGlobalMaskTextures(in PoiMesh poiMesh, inout PoiMods poiMods)
{
#if defined(PROP_GLOBALMASKTEXTURE0)
	vec4 gmcol0 = poiTexPan(_GlobalMaskTexture0, poiUV(poiMesh.uv[int(_GlobalMaskTexture0UV)], _GlobalMaskTexture0_ST), _GlobalMaskTexture0Pan);
	if (float(_GlobalMaskTexture0Split) >= 0.5)
	{
		poiMods.globalMask[0] = gmcol0.r;
		poiMods.globalMask[1] = poiTexPan(_GlobalMaskTexture0, poiUV(poiMesh.uv[int(_GlobalMaskTexture0UV)], _GlobalMaskTexture0SplitTilingOffset_G), _GlobalMaskTexture0SplitPan_G.xy).g;
		poiMods.globalMask[2] = poiTexPan(_GlobalMaskTexture0, poiUV(poiMesh.uv[int(_GlobalMaskTexture0UV)], _GlobalMaskTexture0SplitTilingOffset_B), _GlobalMaskTexture0SplitPan_B.xy).b;
		poiMods.globalMask[3] = poiTexPan(_GlobalMaskTexture0, poiUV(poiMesh.uv[int(_GlobalMaskTexture0UV)], _GlobalMaskTexture0SplitTilingOffset_A), _GlobalMaskTexture0SplitPan_A.xy).a;
	}
	else
	{
		poiMods.globalMask[0] = gmcol0.x;
		poiMods.globalMask[1] = gmcol0.y;
		poiMods.globalMask[2] = gmcol0.z;
		poiMods.globalMask[3] = gmcol0.w;
	}
#endif

#if defined(PROP_GLOBALMASKTEXTURE1)
	vec4 gmcol1 = poiTexPan(_GlobalMaskTexture1, poiUV(poiMesh.uv[int(_GlobalMaskTexture1UV)], _GlobalMaskTexture1_ST), _GlobalMaskTexture1Pan);
	if (float(_GlobalMaskTexture1Split) >= 0.5)
	{
		poiMods.globalMask[4] = gmcol1.r;
		poiMods.globalMask[5] = poiTexPan(_GlobalMaskTexture1, poiUV(poiMesh.uv[int(_GlobalMaskTexture1UV)], _GlobalMaskTexture1SplitTilingOffset_G), _GlobalMaskTexture1SplitPan_G.xy).g;
		poiMods.globalMask[6] = poiTexPan(_GlobalMaskTexture1, poiUV(poiMesh.uv[int(_GlobalMaskTexture1UV)], _GlobalMaskTexture1SplitTilingOffset_B), _GlobalMaskTexture1SplitPan_B.xy).b;
		poiMods.globalMask[7] = poiTexPan(_GlobalMaskTexture1, poiUV(poiMesh.uv[int(_GlobalMaskTexture1UV)], _GlobalMaskTexture1SplitTilingOffset_A), _GlobalMaskTexture1SplitPan_A.xy).a;
	}
	else
	{
		poiMods.globalMask[4] = gmcol1.x;
		poiMods.globalMask[5] = gmcol1.y;
		poiMods.globalMask[6] = gmcol1.z;
		poiMods.globalMask[7] = gmcol1.w;
	}
#endif

#if defined(PROP_GLOBALMASKTEXTURE2)
	vec4 gmcol2 = poiTexPan(_GlobalMaskTexture2, poiUV(poiMesh.uv[int(_GlobalMaskTexture2UV)], _GlobalMaskTexture2_ST), _GlobalMaskTexture2Pan);
	if (float(_GlobalMaskTexture2Split) >= 0.5)
	{
		poiMods.globalMask[8] = gmcol2.r;
		poiMods.globalMask[9] = poiTexPan(_GlobalMaskTexture2, poiUV(poiMesh.uv[int(_GlobalMaskTexture2UV)], _GlobalMaskTexture2SplitTilingOffset_G), _GlobalMaskTexture2SplitPan_G.xy).g;
		poiMods.globalMask[10] = poiTexPan(_GlobalMaskTexture2, poiUV(poiMesh.uv[int(_GlobalMaskTexture2UV)], _GlobalMaskTexture2SplitTilingOffset_B), _GlobalMaskTexture2SplitPan_B.xy).b;
		poiMods.globalMask[11] = poiTexPan(_GlobalMaskTexture2, poiUV(poiMesh.uv[int(_GlobalMaskTexture2UV)], _GlobalMaskTexture2SplitTilingOffset_A), _GlobalMaskTexture2SplitPan_A.xy).a;
	}
	else
	{
		poiMods.globalMask[8] = gmcol2.x;
		poiMods.globalMask[9] = gmcol2.y;
		poiMods.globalMask[10] = gmcol2.z;
		poiMods.globalMask[11] = gmcol2.w;
	}
#endif

#if defined(PROP_GLOBALMASKTEXTURE3)
	vec4 gmcol3 = poiTexPan(_GlobalMaskTexture3, poiUV(poiMesh.uv[int(_GlobalMaskTexture3UV)], _GlobalMaskTexture3_ST), _GlobalMaskTexture3Pan);
	if (float(_GlobalMaskTexture3Split) >= 0.5)
	{
		poiMods.globalMask[12] = gmcol3.r;
		poiMods.globalMask[13] = poiTexPan(_GlobalMaskTexture3, poiUV(poiMesh.uv[int(_GlobalMaskTexture3UV)], _GlobalMaskTexture3SplitTilingOffset_G), _GlobalMaskTexture3SplitPan_G.xy).g;
		poiMods.globalMask[14] = poiTexPan(_GlobalMaskTexture3, poiUV(poiMesh.uv[int(_GlobalMaskTexture3UV)], _GlobalMaskTexture3SplitTilingOffset_B), _GlobalMaskTexture3SplitPan_B.xy).b;
		poiMods.globalMask[15] = poiTexPan(_GlobalMaskTexture3, poiUV(poiMesh.uv[int(_GlobalMaskTexture3UV)], _GlobalMaskTexture3SplitTilingOffset_A), _GlobalMaskTexture3SplitPan_A.xy).a;
	}
	else
	{
		poiMods.globalMask[12] = gmcol3.x;
		poiMods.globalMask[13] = gmcol3.y;
		poiMods.globalMask[14] = gmcol3.z;
		poiMods.globalMask[15] = gmcol3.w;
	}
#endif
}
#endif

void ApplyGlobalMaskOptions(inout PoiMods poiMods)
{
	if (_GlobalMaskOptionsType == 0)
	{
		float sliders[16] = float[16](_GlobalMaskSlider_0, _GlobalMaskSlider_1, _GlobalMaskSlider_2, _GlobalMaskSlider_3,
		_GlobalMaskSlider_4, _GlobalMaskSlider_5, _GlobalMaskSlider_6, _GlobalMaskSlider_7,
		_GlobalMaskSlider_8, _GlobalMaskSlider_9, _GlobalMaskSlider_10, _GlobalMaskSlider_11,
		_GlobalMaskSlider_12, _GlobalMaskSlider_13, _GlobalMaskSlider_14, _GlobalMaskSlider_15);
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] = clamp(poiMods.globalMask[k] + sliders[k], 0.0, 1.0);
		}
	}
	if (_GlobalMaskOptionsType == 1)
	{
		vec2 minMax[16] = vec2[16](_GlobalMaskMinMaxSlider_0, _GlobalMaskMinMaxSlider_1, _GlobalMaskMinMaxSlider_2, _GlobalMaskMinMaxSlider_3,
		_GlobalMaskMinMaxSlider_4, _GlobalMaskMinMaxSlider_5, _GlobalMaskMinMaxSlider_6, _GlobalMaskMinMaxSlider_7,
		_GlobalMaskMinMaxSlider_8, _GlobalMaskMinMaxSlider_9, _GlobalMaskMinMaxSlider_10, _GlobalMaskMinMaxSlider_11,
		_GlobalMaskMinMaxSlider_12, _GlobalMaskMinMaxSlider_13, _GlobalMaskMinMaxSlider_14, _GlobalMaskMinMaxSlider_15);
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] = mix(minMax[k].x, minMax[k].y, poiMods.globalMask[k]);
		}
	}
	if (_GlobalMaskOptionsType == 2)
	{
		int toggleOn[16] = int[16](_GlobalMaskToggleOn_0, _GlobalMaskToggleOn_1, _GlobalMaskToggleOn_2, _GlobalMaskToggleOn_3,
		_GlobalMaskToggleOn_4, _GlobalMaskToggleOn_5, _GlobalMaskToggleOn_6, _GlobalMaskToggleOn_7,
		_GlobalMaskToggleOn_8, _GlobalMaskToggleOn_9, _GlobalMaskToggleOn_10, _GlobalMaskToggleOn_11,
		_GlobalMaskToggleOn_12, _GlobalMaskToggleOn_13, _GlobalMaskToggleOn_14, _GlobalMaskToggleOn_15);
		int toggleOff[16] = int[16](_GlobalMaskToggleOff_0, _GlobalMaskToggleOff_1, _GlobalMaskToggleOff_2, _GlobalMaskToggleOff_3,
		_GlobalMaskToggleOff_4, _GlobalMaskToggleOff_5, _GlobalMaskToggleOff_6, _GlobalMaskToggleOff_7,
		_GlobalMaskToggleOff_8, _GlobalMaskToggleOff_9, _GlobalMaskToggleOff_10, _GlobalMaskToggleOff_11,
		_GlobalMaskToggleOff_12, _GlobalMaskToggleOff_13, _GlobalMaskToggleOff_14, _GlobalMaskToggleOff_15);
		for (int k = 0; k < 16; k++)
		{
			if (float(toggleOn[k]) >= 0.5)
			{
				poiMods.globalMask[k] = 1.0;
			}
		}
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] *= float(1 - toggleOff[k]);
		}
	}
}

float customDistanceBlend(float base, float blend, float blendType)
{
	if (blendType == 0.0)
	{
		return blendNormal(base, blend);
	}
	if (blendType == 2.0)
	{
		return blendMultiply(base, blend);
	}
	return 0.0;
}

void handleGlobalMaskDistance(in PoiCam poiCam, int index, bool enable, bool type, float minAlpha, float maxAlpha, float minDistance, float maxDistance, int blendType, in PoiMesh poiMesh, inout PoiMods poiMods)
{
	if (enable)
	{
		vec3 position = type ? poiMesh.worldPos : poiMesh.objectPosition;
		float val = mix(minAlpha, maxAlpha, smoothstep(minDistance, maxDistance, distance(position, getCameraPosition(poiCam, true))));
		poiMods.globalMask[index] = clamp(customDistanceBlend(poiMods.globalMask[index], val, float(blendType)), 0.0, 1.0);
	}
}

void ApplyGlobalMaskModifiers(in PoiMesh poiMesh, inout PoiMods poiMods, in PoiCam poiCam)
{
	if (_GlobalMaskModifiersBackfaceEnable >= 0.5)
	{
		float facingMode = clamp(poiMesh.isFrontFace, 0.0, 1.0) + 1.0;
		// _GlobalMaskBackface is 0 for ignore, 1 for back only, 2 for front only
		float backface[16] = float[16](_GlobalMaskBackface_0, _GlobalMaskBackface_1, _GlobalMaskBackface_2, _GlobalMaskBackface_3,
		_GlobalMaskBackface_4, _GlobalMaskBackface_5, _GlobalMaskBackface_6, _GlobalMaskBackface_7,
		_GlobalMaskBackface_8, _GlobalMaskBackface_9, _GlobalMaskBackface_10, _GlobalMaskBackface_11,
		_GlobalMaskBackface_12, _GlobalMaskBackface_13, _GlobalMaskBackface_14, _GlobalMaskBackface_15);
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] *= backface[k] == 0.0 ? 1.0 : float(facingMode == backface[k]);
		}
	}

	if (_GlobalMaskModifiersMirrorEnable >= 0.5)
	{
		// Both visibility modes test the oblique mirror projection; there is no separate mirror-mode signal.
		float mirrorMode = IsInMirror(poiCam) ? 1.0 : 0.0;
		mirrorMode += 1.0;
		// _GlobalMaskMirror is 0 for ignore, 1 for outside mirror only, 2 for in mirror only
		float mirror[16] = float[16](_GlobalMaskMirror_0, _GlobalMaskMirror_1, _GlobalMaskMirror_2, _GlobalMaskMirror_3,
		_GlobalMaskMirror_4, _GlobalMaskMirror_5, _GlobalMaskMirror_6, _GlobalMaskMirror_7,
		_GlobalMaskMirror_8, _GlobalMaskMirror_9, _GlobalMaskMirror_10, _GlobalMaskMirror_11,
		_GlobalMaskMirror_12, _GlobalMaskMirror_13, _GlobalMaskMirror_14, _GlobalMaskMirror_15);
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] *= mirror[k] == 0.0 ? 1.0 : float(mirrorMode == mirror[k]);
		}
	}

	if (_GlobalMaskModifiersCameraEnable >= 0.5)
	{
		// No capture-camera signal here: always outside the camera.
		float isCamera = 0.0;
		isCamera += 1.0;
		// _GlobalMaskCamera is 0 for ignore, 1 for outside camera only, 2 for in camera only
		float cameraMode[16] = float[16](_GlobalMaskCamera_0, _GlobalMaskCamera_1, _GlobalMaskCamera_2, _GlobalMaskCamera_3,
		_GlobalMaskCamera_4, _GlobalMaskCamera_5, _GlobalMaskCamera_6, _GlobalMaskCamera_7,
		_GlobalMaskCamera_8, _GlobalMaskCamera_9, _GlobalMaskCamera_10, _GlobalMaskCamera_11,
		_GlobalMaskCamera_12, _GlobalMaskCamera_13, _GlobalMaskCamera_14, _GlobalMaskCamera_15);
		for (int k = 0; k < 16; k++)
		{
			poiMods.globalMask[k] *= cameraMode[k] == 0.0 ? 1.0 : float(isCamera == cameraMode[k]);
		}
	}

	if (float(_GlobalMaskModifiersDistanceEnable) >= 0.5)
	{
		handleGlobalMaskDistance(poiCam, 0, _GlobalMaskDistanceEnable_0 != 0, _GlobalMaskDistanceType_0 != 0, _GlobalMaskDistanceMinAlpha_0, _GlobalMaskDistanceMaxAlpha_0, _GlobalMaskDistanceMin_0, _GlobalMaskDistanceMax_0, _GlobalMaskDistanceBlendType_0, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 1, _GlobalMaskDistanceEnable_1 != 0, _GlobalMaskDistanceType_1 != 0, _GlobalMaskDistanceMinAlpha_1, _GlobalMaskDistanceMaxAlpha_1, _GlobalMaskDistanceMin_1, _GlobalMaskDistanceMax_1, _GlobalMaskDistanceBlendType_1, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 2, _GlobalMaskDistanceEnable_2 != 0, _GlobalMaskDistanceType_2 != 0, _GlobalMaskDistanceMinAlpha_2, _GlobalMaskDistanceMaxAlpha_2, _GlobalMaskDistanceMin_2, _GlobalMaskDistanceMax_2, _GlobalMaskDistanceBlendType_2, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 3, _GlobalMaskDistanceEnable_3 != 0, _GlobalMaskDistanceType_3 != 0, _GlobalMaskDistanceMinAlpha_3, _GlobalMaskDistanceMaxAlpha_3, _GlobalMaskDistanceMin_3, _GlobalMaskDistanceMax_3, _GlobalMaskDistanceBlendType_3, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 4, _GlobalMaskDistanceEnable_4 != 0, _GlobalMaskDistanceType_4 != 0, _GlobalMaskDistanceMinAlpha_4, _GlobalMaskDistanceMaxAlpha_4, _GlobalMaskDistanceMin_4, _GlobalMaskDistanceMax_4, _GlobalMaskDistanceBlendType_4, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 5, _GlobalMaskDistanceEnable_5 != 0, _GlobalMaskDistanceType_5 != 0, _GlobalMaskDistanceMinAlpha_5, _GlobalMaskDistanceMaxAlpha_5, _GlobalMaskDistanceMin_5, _GlobalMaskDistanceMax_5, _GlobalMaskDistanceBlendType_5, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 6, _GlobalMaskDistanceEnable_6 != 0, _GlobalMaskDistanceType_6 != 0, _GlobalMaskDistanceMinAlpha_6, _GlobalMaskDistanceMaxAlpha_6, _GlobalMaskDistanceMin_6, _GlobalMaskDistanceMax_6, _GlobalMaskDistanceBlendType_6, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 7, _GlobalMaskDistanceEnable_7 != 0, _GlobalMaskDistanceType_7 != 0, _GlobalMaskDistanceMinAlpha_7, _GlobalMaskDistanceMaxAlpha_7, _GlobalMaskDistanceMin_7, _GlobalMaskDistanceMax_7, _GlobalMaskDistanceBlendType_7, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 8, _GlobalMaskDistanceEnable_8 != 0, _GlobalMaskDistanceType_8 != 0, _GlobalMaskDistanceMinAlpha_8, _GlobalMaskDistanceMaxAlpha_8, _GlobalMaskDistanceMin_8, _GlobalMaskDistanceMax_8, _GlobalMaskDistanceBlendType_8, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 9, _GlobalMaskDistanceEnable_9 != 0, _GlobalMaskDistanceType_9 != 0, _GlobalMaskDistanceMinAlpha_9, _GlobalMaskDistanceMaxAlpha_9, _GlobalMaskDistanceMin_9, _GlobalMaskDistanceMax_9, _GlobalMaskDistanceBlendType_9, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 10, _GlobalMaskDistanceEnable_10 != 0, _GlobalMaskDistanceType_10 != 0, _GlobalMaskDistanceMinAlpha_10, _GlobalMaskDistanceMaxAlpha_10, _GlobalMaskDistanceMin_10, _GlobalMaskDistanceMax_10, _GlobalMaskDistanceBlendType_10, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 11, _GlobalMaskDistanceEnable_11 != 0, _GlobalMaskDistanceType_11 != 0, _GlobalMaskDistanceMinAlpha_11, _GlobalMaskDistanceMaxAlpha_11, _GlobalMaskDistanceMin_11, _GlobalMaskDistanceMax_11, _GlobalMaskDistanceBlendType_11, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 12, _GlobalMaskDistanceEnable_12 != 0, _GlobalMaskDistanceType_12 != 0, _GlobalMaskDistanceMinAlpha_12, _GlobalMaskDistanceMaxAlpha_12, _GlobalMaskDistanceMin_12, _GlobalMaskDistanceMax_12, _GlobalMaskDistanceBlendType_12, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 13, _GlobalMaskDistanceEnable_13 != 0, _GlobalMaskDistanceType_13 != 0, _GlobalMaskDistanceMinAlpha_13, _GlobalMaskDistanceMaxAlpha_13, _GlobalMaskDistanceMin_13, _GlobalMaskDistanceMax_13, _GlobalMaskDistanceBlendType_13, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 14, _GlobalMaskDistanceEnable_14 != 0, _GlobalMaskDistanceType_14 != 0, _GlobalMaskDistanceMinAlpha_14, _GlobalMaskDistanceMaxAlpha_14, _GlobalMaskDistanceMin_14, _GlobalMaskDistanceMax_14, _GlobalMaskDistanceBlendType_14, poiMesh, poiMods);
		handleGlobalMaskDistance(poiCam, 15, _GlobalMaskDistanceEnable_15 != 0, _GlobalMaskDistanceType_15 != 0, _GlobalMaskDistanceMinAlpha_15, _GlobalMaskDistanceMaxAlpha_15, _GlobalMaskDistanceMin_15, _GlobalMaskDistanceMax_15, _GlobalMaskDistanceBlendType_15, poiMesh, poiMods);
	}
}

void ApplyGlobalMaskVertexColors(in PoiMesh poiMesh, inout PoiMods poiMods)
{
	vec4 vcol = poiMesh.vertexColor;
	if (float(_GlobalMaskVertexColorLinearSpace) >= 0.5)
	{
		vcol.rgb = GammaToLinearSpace(vcol.rgb);
	}
	if (_GlobalMaskVertexColorRed > 0)
	{
		applyToGlobalMask(poiMods, _GlobalMaskVertexColorRed - 1, _GlobalMaskVertexColorRedBlendType, vcol.r);
	}
	if (_GlobalMaskVertexColorGreen > 0)
	{
		applyToGlobalMask(poiMods, _GlobalMaskVertexColorGreen - 1, _GlobalMaskVertexColorGreenBlendType, vcol.g);
	}
	if (_GlobalMaskVertexColorBlue > 0)
	{
		applyToGlobalMask(poiMods, _GlobalMaskVertexColorBlue - 1, _GlobalMaskVertexColorBlueBlendType, vcol.b);
	}
	if (_GlobalMaskVertexColorAlpha > 0)
	{
		applyToGlobalMask(poiMods, _GlobalMaskVertexColorAlpha - 1, _GlobalMaskVertexColorAlphaBlendType, vcol.a);
	}
}

vec2 applyUVSettings(vec2 uv, vec2 tiling, vec2 offset, float angle, vec2 pan, float rotate)
{
	uv = uv * tiling + offset;

	float angleRad = angle * (PI / 180.0);
	float rotateRad = rotate * (PI / 180.0);
	float totalAngle = angleRad + rotateRad * POI_TIME.y;

	if (totalAngle != 0.0)
	{
		vec2 pivot = vec2(0.5, 0.5);
		float cosA = cos(totalAngle);
		float sinA = sin(totalAngle);
		uv -= pivot;
		uv = vec2(uv.x * cosA - uv.y * sinA, uv.x * sinA + uv.y * cosA);
		uv += pivot;
	}

	uv += pan * POI_TIME.y;

	return uv;
}

// fwidth: see the header.
vec2 calculatePolarCoordinate(in PoiMesh poiMesh)
{
	vec2 delta = poiMesh.uv[int(_PolarUV)] - _PolarCenter;
	float dist = length(delta);
	float radius = 0.0;

	// Calculate radius based on mode
	if (_PolarMode == 0.0) // Standard
	{
		radius = dist * 2.0 * _PolarRadialScale;
	}
	else if (_PolarMode == 1.0) // Log Polar - preserves texture scale
	{
		radius = log(dist + 0.001) * _PolarRadialScale + 1.0;
	}
	else // Square Polar
	{
		radius = max(abs(delta.x), abs(delta.y)) * 2.0 * _PolarRadialScale;
	}

	// Calculate angle
	float angle = atan(delta.x, delta.y);
	float phi = angle / (PI * 2.0);
	float phi_frac = fract(phi);
	angle = fwidth(phi) - 0.0001 < fwidth(phi_frac) ? phi : phi_frac;
	angle *= _PolarLengthScale;

	return vec2(radius, angle + dist * _PolarSpiralPower);
}

vec2 MonoPanoProjection(vec3 coords)
{
	vec3 normalizedCoords = normalize(coords);
	float latitude = acos(normalizedCoords.y);
	float longitude = atan(normalizedCoords.z, normalizedCoords.x);
	float phi = longitude / (PI * 2.0);
	float phi_frac = fract(phi);
	longitude = fwidth(phi) - 0.0001 < fwidth(phi_frac) ? phi : phi_frac;
	longitude *= 2.0;
	vec2 sphereCoords = vec2(longitude, latitude) * vec2(1.0, 1.0 / PI);
	return vec2(1.0, 1.0) - sphereCoords;
}

vec2 StereoPanoProjection(in PoiCam poiCam, vec3 coords, int stereoLayout)
{
	vec3 normalizedCoords = normalize(coords);
	float latitude = acos(normalizedCoords.y);
	float longitude = atan(normalizedCoords.z, normalizedCoords.x);
	float phi = longitude / (PI * 2.0);
	float phi_frac = fract(phi);
	longitude = fwidth(phi) - 0.0001 < fwidth(phi_frac) ? phi : phi_frac;
	longitude *= 2.0;
	vec2 sphereCoords = vec2(longitude, latitude) * vec2(0.5, 1.0 / PI);
	sphereCoords = vec2(0.5, 1.0) - sphereCoords;
	if (stereoLayout == 2)
	{
		return vec2(sphereCoords.x * 0.5 + float(poiCam.eyeIndex) * 0.5, sphereCoords.y);
	}
	return vec2(sphereCoords.x, sphereCoords.y * 0.5 + float(1 - poiCam.eyeIndex) * 0.5);
}

vec2 calculateWorldUV(in PoiMesh poiMesh)
{
	vec3 worldPos = poiLeftHanded(poiMesh.worldPos);
	return vec2(_UVModWorldPos0 != 3.0 ? worldPos[int(_UVModWorldPos0)] : 0.0, _UVModWorldPos1 != 3.0 ? worldPos[int(_UVModWorldPos1)] : 0.0);
}

vec2 calculatelocalUV(in PoiMesh poiMesh)
{
	vec3 localPos = poiLeftHanded(poiMesh.localPos);
	float localUVs[8] = float[8](localPos.x, localPos.y, localPos.z, 0.0,
	poiMesh.vertexColor.r, poiMesh.vertexColor.g, poiMesh.vertexColor.b, poiMesh.vertexColor.a);

	return vec2(localUVs[int(_UVModLocalPos0)], localUVs[int(_UVModLocalPos1)]);
}

// fwidth: see the header.
vec2 calculatePanosphereUV(in PoiMesh poiMesh, in PoiCam poiCam, int panoUseBothEyes, int stereoLayout)
{
	bool useCenterEye = panoUseBothEyes <= 0;
	vec3 panoCameraPosition = getCameraPosition(poiCam, useCenterEye);

	vec3 viewDirection = poiLeftHanded(normalize(poiMesh.worldPos.xyz - panoCameraPosition));
	if (stereoLayout != 0)
	{
		return StereoPanoProjection(poiCam, viewDirection, stereoLayout);
	}
	return MonoPanoProjection(viewDirection);
}

vec2 calculateViewNormalUV(in PoiMesh poiMesh, in PoiCam poiCam)
{
	vec3 worldViewUp = normalize(vec3(0.0, 1.0, 0.0) - poiCam.viewDir * dot(poiCam.viewDir, vec3(0.0, 1.0, 0.0)));
	// Operands swapped from the source: in a right-handed world this is the view's right.
	vec3 worldViewRight = normalize(cross(worldViewUp, poiCam.viewDir));
	return vec2(dot(worldViewRight, poiMesh.normals[0]), dot(worldViewUp, poiMesh.normals[0])) * 0.5 + 0.5;
}

// clipPos is the fragment position (pixel xy, depth, w), as the source's SV_Position.
vec2 calculateScreenSpaceUV(in PoiCam poiCam, vec4 clipPos)
{
	vec4 poiScreenSpaceUV = poiTransformClipSpacetoScreenSpaceFrag(poiCam, clipPos);
	vec2 screenUV = poiScreenSpaceUV.xy / poiScreenSpaceUV.w;

	// Per-eye optical center (projection principal point) in UV. Centering the square here instead of
	// the raw screen center (0.5) anchors the texture center to the same forward direction in both eyes
	// so it fuses in stereo. The forward axis projects to NDC -P[2].xy; NDC y runs down the screen
	// (SCREEN_UV * 2 - 1) and screen UV here runs up.
	vec2 ndcCenter = -vec2(poiCam.projectionMatrix[2][0], poiCam.projectionMatrix[2][1]);
	vec2 poiEyeOpticalCenter = vec2(0.5 + 0.5 * ndcCenter.x, 0.5 - 0.5 * ndcCenter.y);

	// Stretch the long axis from eye space so the UV stays square.
	float poiViewAspect = abs(poiCam.projectionMatrix[1][1]) / max(abs(poiCam.projectionMatrix[0][0]), 0.000001);
	vec2 poiScreenSquareScale = vec2(1.0 / max(poiViewAspect, 1.0), min(poiViewAspect, 1.0));

	screenUV = (screenUV - poiEyeOpticalCenter) / poiScreenSquareScale + 0.5;

	return screenUV;
}

#if defined(POI_DISTORTION)
vec2 gerstnerWaveDisplacement(vec2 pos, vec2 dir, float amplitude, float wavelength, float speedMult, float steepness, float time)
{
	float w = TAU / wavelength;
	float phase = _DistortionGerstnerSpeed * speedMult * w;
	float theta = w * dot(dir, pos) + phase * time;
	return steepness * amplitude * dir * cos(theta);
}

vec2 computeGerstnerFlow(vec2 pos, float time)
{
	vec2 flow = vec2(0.0);
	float deg2rad = PI / 180.0;
	vec2 dir1 = vec2(cos(_DistortionGerstnerWave1.x * deg2rad), sin(_DistortionGerstnerWave1.x * deg2rad));
	vec2 dir2 = vec2(cos(_DistortionGerstnerWave2.x * deg2rad), sin(_DistortionGerstnerWave2.x * deg2rad));
	vec2 dir3 = vec2(cos(_DistortionGerstnerWave3.x * deg2rad), sin(_DistortionGerstnerWave3.x * deg2rad));
	vec2 dir4 = vec2(cos(_DistortionGerstnerWave4.x * deg2rad), sin(_DistortionGerstnerWave4.x * deg2rad));
	flow += gerstnerWaveDisplacement(pos, dir1, _DistortionGerstnerWave1.y, _DistortionGerstnerWave1.z, _DistortionGerstnerWave1.w, _DistortionGerstnerSteepness, time);
	flow += gerstnerWaveDisplacement(pos, dir2, _DistortionGerstnerWave2.y, _DistortionGerstnerWave2.z, _DistortionGerstnerWave2.w, _DistortionGerstnerSteepness, time);
	flow += gerstnerWaveDisplacement(pos, dir3, _DistortionGerstnerWave3.y, _DistortionGerstnerWave3.z, _DistortionGerstnerWave3.w, _DistortionGerstnerSteepness, time);
	flow += gerstnerWaveDisplacement(pos, dir4, _DistortionGerstnerWave4.y, _DistortionGerstnerWave4.z, _DistortionGerstnerWave4.w, _DistortionGerstnerSteepness, time);
	return flow;
}

vec2 computeSimplexFlow(vec2 pos, float time)
{
	vec2 samplePos = pos + _DistortionPanSpeed * time;
	float n1 = snoise(samplePos + vec2(time * _DistortionSimplexSpeed, 0.0));
	float n2 = snoise(samplePos + vec2(0.0, time * _DistortionSimplexSpeed) + 100.0);
	return vec2(n1, n2) * _DistortionSimplexStrength;
}

// Voronoi noise
vec2 poiUVD_voronoiHash(vec2 p)
{
	p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
	return fract(sin(p) * 43758.5453);
}

vec2 computeVoronoiFlow(vec2 pos, float time)
{
	vec2 samplePos = pos + _DistortionPanSpeed * time;
	vec2 n = floor(samplePos);
	vec2 f = fract(samplePos);
	vec2 mg = vec2(0.0);
	vec2 mr = vec2(0.0);
	float md = 8.0;
	for (int j = -1; j <= 1; j++)
	{
		for (int i = -1; i <= 1; i++)
		{
			vec2 g = vec2(float(i), float(j));
			vec2 o = poiUVD_voronoiHash(n + g);
			o = 0.5 + 0.5 * sin(time * _DistortionVoronoiSpeed + 6.2831 * o);
			vec2 r = g + o - f;
			float d = dot(r, r);
			if (d < md)
			{
				md = d;
				mr = r;
				mg = g;
			}
		}
	}
	md = 8.0;
	for (int j2 = -2; j2 <= 2; j2++)
	{
		for (int i2 = -2; i2 <= 2; i2++)
		{
			vec2 g = mg + vec2(float(i2), float(j2));
			vec2 o = poiUVD_voronoiHash(n + g);
			o = 0.5 + 0.5 * sin(time * _DistortionVoronoiSpeed + 6.2831 * o);
			vec2 r = g + o - f;
			if (dot(mr - r, mr - r) > 0.00001)
			{
				md = min(md, dot(0.5 * (mr + r), normalize(r - mr)));
			}
		}
	}
	float edge = md;
	vec2 flow = mr * mix(1.0, edge, _DistortionVoronoiEdgeBlend);
	return flow * _DistortionVoronoiStrength;
}

// Curl noise (derived from simplex)
vec2 computeCurlFlow(vec2 pos, float time)
{
	vec2 samplePos = pos + _DistortionPanSpeed * time;
	float eps = 0.01;
	float n1 = snoise(samplePos + vec2(eps, 0.0) + time * _DistortionCurlSpeed);
	float n2 = snoise(samplePos - vec2(eps, 0.0) + time * _DistortionCurlSpeed);
	float n3 = snoise(samplePos + vec2(0.0, eps) + time * _DistortionCurlSpeed);
	float n4 = snoise(samplePos - vec2(0.0, eps) + time * _DistortionCurlSpeed);
	vec2 curl = vec2((n3 - n4) / (2.0 * eps), -(n1 - n2) / (2.0 * eps));
	return curl * _DistortionCurlStrength;
}

// FBM (Fractal Brownian Motion)
vec2 computeFBMFlow(vec2 pos, float time)
{
	vec2 samplePos = pos + _DistortionPanSpeed * time;
	vec2 flow = vec2(0.0);
	float amplitude = 1.0;
	float frequency = 1.0;
	float totalAmplitude = 0.0;
	int octaveCount = min(int(_DistortionFBMOctaves), 8);
	for (int i = 0; i < octaveCount; i++)
	{
		vec2 p = samplePos * frequency + time * _DistortionFBMSpeed * float(i + 1) * 0.5;
		float n1 = snoise(p);
		float n2 = snoise(p + 100.0);
		flow += vec2(n1, n2) * amplitude;
		totalAmplitude += amplitude;
		frequency *= _DistortionFBMLacunarity;
		amplitude *= _DistortionFBMGain;
	}
	return (flow / totalAmplitude) * _DistortionFBMStrength;
}

vec2 distortedUV(in PoiMesh poiMesh)
{
#if defined(PROP_DISTORTIONMASK)
	float distortionMask = poiTexPan(_DistortionMask, poiMesh.uv[int(_DistortionMaskUV)], _DistortionMaskPan)[int(_DistortionMaskChannel)];
#else
	float distortionMask = 1.0;
#endif

	vec2 flowOffset = vec2(0.0);
	vec2 worldXZ = poiLeftHanded(poiMesh.worldPos).xz;

	if (_DistortionMode == 0.0)
	{
#if defined(PROP_DISTORTIONFLOWTEXTURE)
		vec4 flowVector = poiTexPan(_DistortionFlowTexture, poiUV(poiMesh.uv[int(_DistortionFlowTextureUV)], _DistortionFlowTexture_ST), _DistortionFlowTexturePan) * 2.0 - 1.0;
#else
		vec4 flowVector = vec4(-1.0);
#endif

#if defined(PROP_DISTORTIONFLOWTEXTURE1)
		vec4 flowVector1 = poiTexPan(_DistortionFlowTexture1, poiUV(poiMesh.uv[int(_DistortionFlowTexture1UV)], _DistortionFlowTexture1_ST), _DistortionFlowTexture1Pan) * 2.0 - 1.0;
#else
		vec4 flowVector1 = vec4(-1.0);
#endif

		float distortionStrength = _DistortionStrength;
		float distortionStrength1 = _DistortionStrength1;

		flowVector *= distortionStrength;
		flowVector1 *= distortionStrength1;
		flowOffset = (flowVector.xy + flowVector1.xy) / 2.0;
	}
	else if (_DistortionMode == 1.0)
	{
		vec2 samplePos = worldXZ * _DistortionTiling * _DistortionGerstnerScale + _DistortionPanSpeed * POI_TIME.y;
		flowOffset = computeGerstnerFlow(samplePos, POI_TIME.y) * _DistortionGerstnerStrength;
	}
	else if (_DistortionMode == 2.0)
	{
		vec2 samplePos = worldXZ * _DistortionTiling * _DistortionSimplexScale;
		flowOffset = computeSimplexFlow(samplePos, POI_TIME.y);
	}
	else if (_DistortionMode == 3.0)
	{
		vec2 samplePos = worldXZ * _DistortionTiling * _DistortionVoronoiScale;
		flowOffset = computeVoronoiFlow(samplePos, POI_TIME.y);
	}
	else if (_DistortionMode == 4.0)
	{
		vec2 samplePos = worldXZ * _DistortionTiling * _DistortionCurlScale;
		flowOffset = computeCurlFlow(samplePos, POI_TIME.y);
	}
	else if (_DistortionMode == 5.0)
	{
		vec2 samplePos = worldXZ * _DistortionTiling * _DistortionFBMScale;
		flowOffset = computeFBMFlow(samplePos, POI_TIME.y);
	}

	return poiMesh.uv[int(_DistortionUvToDistort)] + flowOffset * distortionMask;
}
#endif

#if defined(POI_UDIMDISCARD)
// facing: 1 front, 0 back.
void applyUDIMDiscard(in VertexOut i, uint facing)
{
	// UDIM Discard
	if (_UDIMDiscardMode == 1.0) // Don't run if in vertex mode
	{
		vec2 udim = floor(vertexUV(i, int(_UDIMDiscardUV)));

		vec4 UDIMDiscardRows[4];
		UDIMDiscardRows[0] = vec4(_UDIMDiscardRow0_0, _UDIMDiscardRow0_1, _UDIMDiscardRow0_2, _UDIMDiscardRow0_3);
		UDIMDiscardRows[1] = vec4(_UDIMDiscardRow1_0, _UDIMDiscardRow1_1, _UDIMDiscardRow1_2, _UDIMDiscardRow1_3);
		UDIMDiscardRows[2] = vec4(_UDIMDiscardRow2_0, _UDIMDiscardRow2_1, _UDIMDiscardRow2_2, _UDIMDiscardRow2_3);
		UDIMDiscardRows[3] = vec4(_UDIMDiscardRow3_0, _UDIMDiscardRow3_1, _UDIMDiscardRow3_2, _UDIMDiscardRow3_3);

		float shouldDiscard = shouldPerformUDIMDiscard(udim, UDIMDiscardRows);

		if (shouldDiscard < 0.0)
		{
			discard;
		}
	}

	// UDIM Face Discard
	if (_EnableUDIMFaceDiscardOptions >= 0.5 && float(min(facing, 1u)) == _UDIMFaceDiscardFace) // Run if Face Discard enabled & this is the face to discard
	{
		vec2 faceUdim = floor(vertexUV(i, int(_UDIMFaceDiscardUV)));

		vec4 UDIMFaceDiscardRows[4];
		UDIMFaceDiscardRows[0] = vec4(_UDIMFaceDiscardRow0_0, _UDIMFaceDiscardRow0_1, _UDIMFaceDiscardRow0_2, _UDIMFaceDiscardRow0_3);
		UDIMFaceDiscardRows[1] = vec4(_UDIMFaceDiscardRow1_0, _UDIMFaceDiscardRow1_1, _UDIMFaceDiscardRow1_2, _UDIMFaceDiscardRow1_3);
		UDIMFaceDiscardRows[2] = vec4(_UDIMFaceDiscardRow2_0, _UDIMFaceDiscardRow2_1, _UDIMFaceDiscardRow2_2, _UDIMFaceDiscardRow2_3);
		UDIMFaceDiscardRows[3] = vec4(_UDIMFaceDiscardRow3_0, _UDIMFaceDiscardRow3_1, _UDIMFaceDiscardRow3_2, _UDIMFaceDiscardRow3_3);

		float shouldDiscard = shouldPerformUDIMDiscard(faceUdim, UDIMFaceDiscardRows);

		if (shouldDiscard < 0.0)
		{
			discard;
		}
	}
}
#endif

#if defined(POI_PARALLAX) && defined(PROP_HEIGHTMAP)
// The source's height map argument is dropped: its body always reads _HeightMap.
float SampleHeightAnisotropic(vec2 uv, vec2 dx, vec2 dy, float mipBias, float anisotropyScale)
{
	float mipScale = pow(2.0, mipBias);
	dx *= mipScale;
	dy *= mipScale;

	if (_ParallaxAnisotropic >= 0.5)
	{
		vec2 dxScaled = dx * anisotropyScale;
		vec2 dyScaled = dy * anisotropyScale;
		return poiTexGrad(_HeightMap, uv, dxScaled, dyScaled).r;
	}
	return poiTexGrad(_HeightMap, uv, dx, dy).r;
}

void CalculateAnisotropicDerivatives(vec2 baseUV, vec2 currentUV, vec2 baseDx, vec2 baseDy, float parallaxStrength, float viewDotNormal, out vec2 dx, out vec2 dy)
{
	vec2 uvOffset = currentUV - baseUV;
	float offsetLength = length(uvOffset);

	// More offset = more blur needed
	float angleCompensation = 1.0 + (1.0 - viewDotNormal) * 2.0;
	float anisotropicFactor = 1.0 + offsetLength * parallaxStrength * angleCompensation;

	dx = baseDx * anisotropicFactor;
	dy = baseDy * anisotropicFactor;
}

// Fragment only: derivatives.
vec2 ParallaxOcclusionMapping(in PoiCam poiCam, in PoiLight poiLight, in PoiMesh poiMesh, inout PoiMods poiMods, vec3 worldViewDir, vec3 viewDirTan, int minSamples, int maxSamples, float parallaxStrength, float heightOffset, vec2 curvature)
{
	if (parallaxStrength <= 0.0)
	{
		return poiMesh.uv[int(_ParallaxUV)];
	}

	// Check mask first - saves work if masked out
	float heightMask = 1.0;
#if defined(PROP_HEIGHTMASK)
	vec2 maskUV = poiUV(poiMesh.uv[int(_HeightmaskUV)], _Heightmask_ST) + _HeightmaskPan * POI_TIME.x;
	float maskSample = poiTex(_Heightmask, maskUV)[int(_HeightmaskChannel)];
	heightMask = _HeightmaskInvert != 0.0 ? 1.0 - maskSample : maskSample;
	if (_HeightmaskGlobalMask > 0.0)
	{
		heightMask = maskBlend(heightMask, poiMods.globalMask[int(_HeightmaskGlobalMask) - 1], _HeightmaskGlobalMaskBlendType);
	}
#endif

	if (heightMask <= 0.0)
	{
		return poiMesh.uv[int(_ParallaxUV)];
	}

	vec2 baseUV = poiMesh.uv[int(_ParallaxUV)];
	vec2 heightmapUV = poiUV(poiMesh.uv[int(_HeightMapUV)], _HeightMap_ST);

	vec2 baseDx = dFdx(heightmapUV);
	vec2 baseDy = dFdy(heightmapUV);
	vec2 dx = baseDx;
	vec2 dy = baseDy;

	// Fewer steps when looking straight down
	float viewDotNormal = clamp(dot(poiMesh.normals[0], worldViewDir), 0.0, 1.0);

	float parallaxCamDist = distance(poiMesh.worldPos, getCameraPosition(poiCam, false));
	float parallaxLOD = clamp((parallaxCamDist - _ParallaxLOD.x) / max(_ParallaxLOD.y - _ParallaxLOD.x, 0.0001), 0.0, 1.0);
	int lodMaxSamples = int(mix(float(maxSamples), float(minSamples), parallaxLOD));

	int numSteps = int(mix(float(lodMaxSamples), float(minSamples), viewDotNormal));
	numSteps = max(numSteps, 1);

	float layerHeight = 1.0 / float(numSteps);
	float viewDirZ = max(abs(viewDirTan.z), 0.001);
	vec2 offsetScale = parallaxStrength * heightMask * (viewDirTan.xy / viewDirZ);
	vec2 deltaUV = -offsetScale * layerHeight;

	vec2 currentUV = baseUV + heightOffset * offsetScale;
	float currentRayZ = 1.0 - heightOffset;
	float currentHeight = 0.0;

	vec2 prevUV = currentUV;
	float prevRayZ = currentRayZ;
	float prevHeight = 0.0;

	// Cone stepping lets us take bigger steps when far from surface
	float coneRatio = _ParallaxRelaxedCone != 0.0 ? _ParallaxConeRatio : 1.0;
	float currentConeRadius = 0.0;
	float stepMultiplier = 1.0;

	bool intersectionFound = false;
	for (int i = 0; i < numSteps && !intersectionFound; i++)
	{
		vec2 uvOffset = currentUV - baseUV;
		float curvatureEffect = dot(curvature, uvOffset * uvOffset);
		float curvatureFactor = 1.0 - clamp(curvatureEffect, 0.0, 1.0);

		if (_ParallaxAnisotropic >= 0.5)
		{
			CalculateAnisotropicDerivatives(baseUV, currentUV, baseDx, baseDy, parallaxStrength, viewDotNormal, dx, dy);
		}

		vec2 sampleUV = heightmapUV + (uvOffset * _HeightMap_ST.xy) + _HeightMapPan * POI_TIME.x;
		currentHeight = SampleHeightAnisotropic(sampleUV, dx, dy, _ParallaxMipBias, _ParallaxAnisotropyScale) * curvatureFactor;

		if (_ParallaxRelaxedCone >= 0.5)
		{
			currentConeRadius = currentRayZ * coneRatio * layerHeight;
			float heightDifference = currentRayZ - currentHeight;

			if (heightDifference < currentConeRadius)
			{
				stepMultiplier = max(0.5, heightDifference / currentConeRadius);

				if (currentHeight > currentRayZ)
				{
					intersectionFound = true;
				}
			}
			else
			{
				stepMultiplier = min(2.0, heightDifference / currentConeRadius);
			}
		}
		else
		{
			if (currentHeight > currentRayZ)
			{
				intersectionFound = true;
			}
		}

		if (!intersectionFound)
		{
			prevUV = currentUV;
			prevRayZ = currentRayZ;
			prevHeight = currentHeight;

			vec2 adaptiveDeltaUV = deltaUV * stepMultiplier;
			currentUV += adaptiveDeltaUV;

			float curvatureCompensation = curvatureFactor * (1.0 + _CurvFix * 0.1);
			currentRayZ -= layerHeight * stepMultiplier * curvatureCompensation;
		}
	}

	// Binary search to find exact intersection point
	if (intersectionFound)
	{
		int binarySteps = int(_ParallaxBinarySteps);

		for (int j = 0; j < binarySteps; j++)
		{
			float denominator = prevHeight - currentHeight + currentRayZ - prevRayZ + 0.0001;
			float intersection = clamp((prevHeight - prevRayZ) / denominator, 0.0, 1.0);

			// Don't overshoot
			intersection = mix(0.25, 0.75, intersection);

			vec2 testUV = mix(prevUV, currentUV, intersection);
			float testRayZ = mix(prevRayZ, currentRayZ, intersection);

			vec2 uvOffset = testUV - baseUV;
			float curvatureEffect = dot(curvature, uvOffset * uvOffset);
			float curvatureFactor = 1.0 - clamp(curvatureEffect, 0.0, 1.0);

			if (_ParallaxAnisotropic >= 0.5)
			{
				CalculateAnisotropicDerivatives(baseUV, testUV, baseDx, baseDy, parallaxStrength, viewDotNormal, dx, dy);
			}

			vec2 sampleUV = heightmapUV + (uvOffset * _HeightMap_ST.xy) + _HeightMapPan * POI_TIME.x;
			float testHeight = SampleHeightAnisotropic(sampleUV, dx, dy, _ParallaxMipBias, _ParallaxAnisotropyScale) * curvatureFactor;

			if (testHeight > testRayZ)
			{
				currentUV = testUV;
				currentHeight = testHeight;
				currentRayZ = testRayZ;
			}
			else
			{
				prevUV = testUV;
				prevHeight = testHeight;
				prevRayZ = testRayZ;
			}

			float error = abs(testHeight - testRayZ);
			if (error < 0.001)
			{
				break;
			}
		}
	}

	return currentUV;
}

void applyParallax(inout PoiMesh poiMesh, in PoiLight poiLight, in PoiCam poiCam, inout PoiMods poiMods)
{
	vec2 parallaxUV = ParallaxOcclusionMapping(poiCam, poiLight, poiMesh, poiMods, poiCam.viewDir, poiCam.tangentViewDir, int(_HeightStepsMin), int(_HeightStepsMax), _HeightStrength, _HeightOffset, vec2(_CurvatureU, _CurvatureV));
	poiMesh.uv[int(_ParallaxUV)] = parallaxUV;
}
#endif

#if defined(POI_BLACKLIGHTMASKING)
// The source keys black lights off its per-vertex point lights. Every light here is per pixel, which is
// the source's no-vertex-light path: each assigned mask takes 0.
void calculateBlackLightMasks(in PoiMesh poiMesh, inout PoiMods poiMods)
{
	if (_BlackLightMasking0GlobalMaskIndex > 0.0)
	{
		applyToGlobalMask(poiMods, int(_BlackLightMasking0GlobalMaskIndex) - 1, int(_BlackLightMasking0GlobalMaskBlendType), 0.0);
	}

	if (_BlackLightMasking1GlobalMaskIndex > 0.0)
	{
		applyToGlobalMask(poiMods, int(_BlackLightMasking1GlobalMaskIndex) - 1, int(_BlackLightMasking1GlobalMaskBlendType), 0.0);
	}

	if (_BlackLightMasking2GlobalMaskIndex > 0.0)
	{
		applyToGlobalMask(poiMods, int(_BlackLightMasking2GlobalMaskIndex) - 1, int(_BlackLightMasking2GlobalMaskBlendType), 0.0);
	}

	if (_BlackLightMasking3GlobalMaskIndex > 0.0)
	{
		applyToGlobalMask(poiMods, int(_BlackLightMasking3GlobalMaskIndex) - 1, int(_BlackLightMasking3GlobalMaskBlendType), 0.0);
	}
}
#endif

#if defined(POI_DETAIL)
void ApplyDetailColor(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiMods poiMods)
{
#if defined(PROP_DETAILTEX)
	vec3 detailTexture = poiTexPanStochastic(_DetailTex, poiUV(poiMesh.uv[int(_DetailTexUV)], _DetailTex_ST), _DetailTexPan, _DetailTexStochastic).rgb * poiThemeColor(poiMods, _DetailTint.rgb, _DetailTintThemeIndex);
#else
	vec3 detailTexture = 0.21763764082 * poiThemeColor(poiMods, _DetailTint.rgb, _DetailTintThemeIndex);
#endif

	poiFragData.baseColor.rgb *= LerpWhiteTo(detailTexture * _DetailBrightness * POI_COLOR_SPACE_DOUBLE.rgb, poiMods.detailMask.r * _DetailTexIntensity);
}

void ApplyDetailNormal(inout PoiMods poiMods, inout PoiMesh poiMesh)
{
#if defined(PROP_DETAILMASK)
	poiMods.detailMask = poiTexPanStochastic(_DetailMask, poiUV(poiMesh.uv[int(_DetailMaskUV)], _DetailMask_ST), _DetailMaskPan, _DetailMaskStochastic).rg;
#else
	poiMods.detailMask = vec2(1.0);
#endif

#if defined(POI_BACKFACE)
	if (poiMesh.isFrontFace == 0.0)
	{
		poiMods.detailMask.rg *= _BackFaceDetailIntensity;
	}
#endif

	if (_DetailTexGlobalMask > 0.0)
	{
		poiMods.detailMask.r = maskBlend(poiMods.detailMask.r, poiMods.globalMask[int(_DetailTexGlobalMask) - 1], _DetailTexGlobalMaskBlendType);
	}
	if (_DetailNormalGlobalMask > 0.0)
	{
		poiMods.detailMask.g = maskBlend(poiMods.detailMask.g, poiMods.globalMask[int(_DetailNormalGlobalMask) - 1], _DetailNormalGlobalMaskBlendType);
	}

#if defined(PROP_DETAILNORMALMAP)
	vec3 detailNormal = PoiUnpackNormalScale(poiTexPanStochastic(_DetailNormalMap, poiUV(poiMesh.uv[int(_DetailNormalMapUV)], _DetailNormalMap_ST), _DetailNormalMapPan, _DetailNormalMapStochastic), _DetailNormalMapScale * poiMods.detailMask.g);
	poiMesh.tangentSpaceNormal = PoiBlendNormal(detailNormal, poiMesh.tangentSpaceNormal);
#endif
}
#endif

#if defined(POI_SKIN)
#if defined(PROP_POISKINLUT)
// The source's LUT argument is dropped: its one caller passes _PoiSkinLUT.
vec3 PoiSampleSkinLUT(float melanin, float melaninRedness, float hemoglobin)
{
	// Skip non-monotonic zones at LUT edges
	melanin = mix(0.08, 0.92, melanin);

	float blend = (1.0 - melaninRedness) * 2.0;
	int panelA = min(int(blend), 1);
	int panelB = panelA + 1;
	float panelLerp = blend - float(panelA);

	float margin = 1.0 / 384.0;
	float pw = 1.0 / 3.0;

	float uA = float(panelA) * pw + margin + (pw - 2.0 * margin) * melanin;
	float uB = float(panelB) * pw + margin + (pw - 2.0 * margin) * melanin;

	float v = 1.0 - hemoglobin;

	vec3 colorA = poiTexLod(_PoiSkinLUT, poiMainClampUV(_PoiSkinLUT, vec2(uA, v)), 0.0).rgb;
	vec3 colorB = poiTexLod(_PoiSkinLUT, poiMainClampUV(_PoiSkinLUT, vec2(uB, v)), 0.0).rgb;
	return mix(colorA, colorB, panelLerp);
}
#endif

void applySkin(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiMods poiMods)
{
	float localMelanin = _PoiSkinMelanin;
	float localMelaninRedness = _PoiSkinMelaninRedness;
	float localHemoglobin = _PoiSkinHemoglobin;

#if defined(PROP_POISKINDETAILMAP)
	// Freckles add localized melanin
	if (_PoiSkinFrecklesEnabled >= 0.5)
	{
		vec2 freckleUV = poiMesh.uv[int(_PoiSkinFreckleMapUV)].xy * _PoiSkinFreckleMapTiling.xy;
		float freckleMask = poiTex(_PoiSkinDetailMap, freckleUV).g;
		freckleMask = pow(freckleMask, _PoiSkinFreckleSharpness) * _PoiSkinFreckleStrength;
		localMelanin += freckleMask;
		localMelaninRedness += freckleMask * _PoiSkinFreckleRedness;
	}
#endif

	// Tan - increases melanin, shifts redness toward eumelanin
	if (_PoiSkinTanEnabled >= 0.5)
	{
		float tanMask = 1.0;
#if defined(PROP_POISKINDETAILMAP)
		vec2 tanUV = poiMesh.uv[int(_PoiSkinTanMapUV)].xy * _PoiSkinTanMapTiling.xy;
		tanMask = poiTex(_PoiSkinDetailMap, tanUV).b;
#endif
		localMelanin += _PoiSkinTanAmount * tanMask;
		localMelaninRedness += _PoiSkinTanRednessShift * tanMask;
	}

	localMelanin = clamp(localMelanin, 0.0, 1.0);
	localMelaninRedness = clamp(localMelaninRedness, 0.0, 1.0);
	localHemoglobin = clamp(localHemoglobin, 0.0, 1.0);

	vec3 skinColor = vec3(1.0, 1.0, 1.0);
#if defined(PROP_POISKINLUT)
	skinColor = PoiSampleSkinLUT(localMelanin, localMelaninRedness, localHemoglobin);
#endif

	skinColor *= _PoiSkinBrightness;
	skinColor *= poiThemeColor(poiMods, _PoiSkinColorTint.rgb, _PoiSkinColorTintThemeIndex);

	// Mask (R channel) - black = no masking (full effect), white = masked out
	float skinBlend = _PoiSkinBlend;
#if defined(PROP_POISKINDETAILMAP)
	float mask = 1.0 - poiTex(_PoiSkinDetailMap, poiMesh.uv[0].xy).r;
	skinBlend *= mask;
#endif

	if (_PoiSkinBlendMode == 0.0)
	{
		poiFragData.baseColor = mix(poiFragData.baseColor, skinColor, skinBlend);
	}
	else
	{
		poiFragData.baseColor = mix(poiFragData.baseColor, poiFragData.baseColor * skinColor, skinBlend);
	}
}
#endif

void applyVertexColor(inout PoiFragData poiFragData, in PoiMesh poiMesh)
{
	if (_MainVertexColoringEnabled >= 0.5)
	{
#if !defined(POI_PASS_OUTLINE)
		vec3 vertCol = mix(poiMesh.vertexColor.rgb, GammaToLinearSpace(poiMesh.vertexColor.rgb), _MainVertexColoringLinearSpace);
		poiFragData.baseColor *= mix(vec3(1.0), vertCol, _MainVertexColoring);
#endif
		poiFragData.alpha *= mix(1.0, poiMesh.vertexColor.a, _MainUseVertexColorAlpha);
	}
}

#if defined(POI_BACKFACE)
void ApplyBackFaceColor(inout PoiFragData poiFragData, in PoiMesh poiMesh, inout PoiMods poiMods)
{
	if (poiMesh.isFrontFace == 0.0)
	{
		vec4 backFaceColor = _BackFaceColor;
		backFaceColor.rgb = poiThemeColor(poiMods, backFaceColor.rgb, _BackFaceColorThemeIndex);
#if defined(PROP_BACKFACETEXTURE)
		backFaceColor *= poiTexPan(_BackFaceTexture, poiUV(poiMesh.uv[int(_BackFaceTextureUV)], _BackFaceTexture_ST), _BackFaceTexturePan);
#endif

		if (_BackFaceHueShiftEnabled >= 0.5)
		{
			backFaceColor.rgb = hueShift(backFaceColor.rgb, fract(_BackFaceHueShift + _BackFaceHueShiftSpeed * POI_TIME.x), _BackFaceShiftColorSpace, _BackFaceHueSelectOrShift);
		}

		float backFaceMask = 1.0;
#if defined(PROP_BACKFACEMASK)
		backFaceMask *= poiTexPan(_BackFaceMask, poiUV(poiMesh.uv[int(_BackFaceMaskUV)], _BackFaceMask_ST), _BackFaceMaskPan)[int(_BackFaceMaskChannel)];
#endif
		if (_BackFaceReplaceAlpha < 0.5)
		{
			backFaceMask *= backFaceColor.a;
		}

		poiFragData.baseColor = mix(poiFragData.baseColor, backFaceColor.rgb, backFaceMask);

		if (_BackFaceReplaceAlpha >= 0.5)
		{
			poiFragData.alpha = backFaceColor.a;
		}

		poiFragData.emission += backFaceColor.rgb * _BackFaceEmissionStrength * backFaceMask;
		poiMods.globalEmission = poiMods.globalEmission * _BackFaceEmissionLimiter;
	}
}
#endif

vec4 PoiUVMerge(in vec4 uV, in vec4 uVToMerge)
{
	uV.xy *= uVToMerge.xy;
	uV.zw += uVToMerge.zw;
	return uV;
}

void RGBABlendColor(inout PoiFragData poiFragData, in float mask, in vec4 color, float emissionStrength, in float blendType, in float blendAdd, in float enabled)
{
	if (enabled == 0.0)
	{
		return;
	}
	float alpha = mask * clamp(color.a + blendAdd, 0.0, 1.0);
	poiFragData.baseColor = mix(poiFragData.baseColor.rgb, customBlend(poiFragData.baseColor.rgb, color.rgb, blendType, 1.0), alpha);
	poiFragData.emission += color.rgb * emissionStrength * alpha;
}

void RGBABlendNormals(inout vec3 tangentSpaceNormal, in vec3 originalNormal, vec3 normalToBlendWith, float maskValue, int blendMode)
{
	if (blendMode == 0)
	{
		tangentSpaceNormal = mix(tangentSpaceNormal, normalToBlendWith, maskValue);
	}
	else if (blendMode == 1)
	{
		tangentSpaceNormal = mix(tangentSpaceNormal, PoiBlendNormal(tangentSpaceNormal, normalToBlendWith), maskValue);
	}
	else if (blendMode == 2)
	{
		tangentSpaceNormal = mix(tangentSpaceNormal, PoiBlendNormal(originalNormal, normalToBlendWith), maskValue);
	}
}

#if defined(POI_RGBMASK)
#if !defined(POI_PASS_OUTLINE)
void calculateRGBNormals(inout PoiMesh poiMesh, inout PoiMods poiMods)
{
	// Only define this if we actually have any normal map textures. Can't do the same in color textures because users can tint
#if defined(PROP_RGBNORMALR) || defined(PROP_RGBNORMALG) || defined(PROP_RGBNORMALB) || defined(PROP_RGBNORMALA)
	vec4 rgbMask = vec4(1.0);

#if defined(PROP_RGBMASK)
	if (_RGBMaskType == 0.0)
	{
		rgbMask = poiTexPan(_RGBMask, poiUV(poiMesh.uv[int(_RGBMaskUV)], _RGBMask_ST), _RGBMaskPan);
	}
#endif

	if (_RGBMaskType == 1.0)
	{
		rgbMask = poiMesh.vertexColor;
	}

	vec4 maskFinal = vec4(1.0);
	maskFinal.r = rgbMask[int(_RgbNormalRMaskChannel)];
	maskFinal.g = rgbMask[int(_RgbNormalGMaskChannel)];
	maskFinal.b = rgbMask[int(_RgbNormalBMaskChannel)];
	maskFinal.a = rgbMask[int(_RgbNormalAMaskChannel)];

	if (_RgbNormalRGlobalMaskChannel > 0.0) maskFinal.r = globalMaskBlend(maskFinal.r, _RgbNormalRGlobalMaskChannel, _RgbNormalRGlobalMaskBlendType, poiMods);
	if (_RgbNormalGGlobalMaskChannel > 0.0) maskFinal.g = globalMaskBlend(maskFinal.g, _RgbNormalGGlobalMaskChannel, _RgbNormalGGlobalMaskBlendType, poiMods);
	if (_RgbNormalBGlobalMaskChannel > 0.0) maskFinal.b = globalMaskBlend(maskFinal.b, _RgbNormalBGlobalMaskChannel, _RgbNormalBGlobalMaskBlendType, poiMods);
	if (_RgbNormalAGlobalMaskChannel > 0.0) maskFinal.a = globalMaskBlend(maskFinal.a, _RgbNormalAGlobalMaskChannel, _RgbNormalAGlobalMaskBlendType, poiMods);

	vec3 originalNormal = poiMesh.tangentSpaceNormal;
	const vec3 neutralNormal = vec3(0.0, 0.0, 1.0);
	vec3 nR = vec3(0.0);
	vec3 nG = vec3(0.0);
	vec3 nB = vec3(0.0);
	float hasR = 0.0;
	float hasG = 0.0;
	float hasB = 0.0;

#if defined(PROP_RGBNORMALR)
	if (_RgbNormalRScale > 0.0 && _RGBARedEnable >= 0.5)
	{
		nR = PoiUnpackNormalScale(poiTexPanStochastic(_RgbNormalR, poiUV(poiMesh.uv[int(_RgbNormalRUV)], PoiUVMerge(_RgbNormalR_ST, _RGBARedScaleOffset)), _RgbNormalRPan, _RgbNormalRStochastic), _RgbNormalRScale);
		hasR = 1.0;
		bool selBaseR = (_RgbNormalRBlendSources.x > 0.5);
		int modeR = selBaseR ? 2 : 0; // selection empty => Replace
		vec3 fromNormalR = selBaseR ? originalNormal : neutralNormal;
		RGBABlendNormals(poiMesh.tangentSpaceNormal, fromNormalR, nR, maskFinal.r, modeR);
	}
#endif
#if defined(PROP_RGBNORMALG)
	if (_RgbNormalGScale > 0.0 && _RGBAGreenEnable >= 0.5)
	{
		nG = PoiUnpackNormalScale(poiTexPanStochastic(_RgbNormalG, poiUV(poiMesh.uv[int(_RgbNormalGUV)], PoiUVMerge(_RgbNormalG_ST, _RGBAGreenScaleOffset)), _RgbNormalGPan, _RgbNormalGStochastic), _RgbNormalGScale);
		hasG = 1.0;
		bool selBaseG = (_RgbNormalGBlendSources.x > 0.5);
		bool selRedG = (_RgbNormalGBlendSources.y > 0.5);
		int modeG = (selBaseG || selRedG) ? 2 : 0;
		vec3 fromNormalG = selBaseG ? originalNormal : neutralNormal;
		if (selRedG && hasR > 0.5) fromNormalG = mix(fromNormalG, PoiBlendNormal(fromNormalG, nR), maskFinal.r);
		RGBABlendNormals(poiMesh.tangentSpaceNormal, fromNormalG, nG, maskFinal.g, modeG);
	}
#endif
#if defined(PROP_RGBNORMALB)
	if (_RgbNormalBScale > 0.0 && _RGBABlueEnable >= 0.5)
	{
		nB = PoiUnpackNormalScale(poiTexPanStochastic(_RgbNormalB, poiUV(poiMesh.uv[int(_RgbNormalBUV)], PoiUVMerge(_RgbNormalB_ST, _RGBABlueScaleOffset)), _RgbNormalBPan, _RgbNormalBStochastic), _RgbNormalBScale);
		hasB = 1.0;
		bool selBaseB = (_RgbNormalBBlendSources.x > 0.5);
		bool selRedB = (_RgbNormalBBlendSources.y > 0.5);
		bool selGreenB = (_RgbNormalBBlendSources.z > 0.5);
		int modeB = (selBaseB || selRedB || selGreenB) ? 2 : 0;
		vec3 fromNormalB = selBaseB ? originalNormal : neutralNormal;
		if (selRedB && hasR > 0.5) fromNormalB = mix(fromNormalB, PoiBlendNormal(fromNormalB, nR), maskFinal.r);
		if (selGreenB && hasG > 0.5) fromNormalB = mix(fromNormalB, PoiBlendNormal(fromNormalB, nG), maskFinal.g);
		RGBABlendNormals(poiMesh.tangentSpaceNormal, fromNormalB, nB, maskFinal.b, modeB);
	}
#endif
#if defined(PROP_RGBNORMALA)
	if (_RgbNormalAScale > 0.0 && _RGBAAlphaEnable >= 0.5)
	{
		vec3 normalToBlendWith = PoiUnpackNormalScale(poiTexPanStochastic(_RgbNormalA, poiUV(poiMesh.uv[int(_RgbNormalAUV)], PoiUVMerge(_RgbNormalA_ST, _RGBAAlphaScaleOffset)), _RgbNormalAPan, _RgbNormalAStochastic), _RgbNormalAScale);
		bool selBaseA = (_RgbNormalABlendSources.x > 0.5);
		bool selRedA = (_RgbNormalABlendSources.y > 0.5);
		bool selGreenA = (_RgbNormalABlendSources.z > 0.5);
		bool selBlueA = (_RgbNormalABlendSources.w > 0.5);
		int modeA = (selBaseA || selRedA || selGreenA || selBlueA) ? 2 : 0;
		vec3 fromNormalA = selBaseA ? originalNormal : neutralNormal;
		if (selRedA && hasR > 0.5) fromNormalA = mix(fromNormalA, PoiBlendNormal(fromNormalA, nR), maskFinal.r);
		if (selGreenA && hasG > 0.5) fromNormalA = mix(fromNormalA, PoiBlendNormal(fromNormalA, nG), maskFinal.g);
		if (selBlueA && hasB > 0.5) fromNormalA = mix(fromNormalA, PoiBlendNormal(fromNormalA, nB), maskFinal.b);
		RGBABlendNormals(poiMesh.tangentSpaceNormal, fromNormalA, normalToBlendWith, maskFinal.a, modeA);
	}
#endif
#endif
}
#endif

void calculateRGBMask(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiMods poiMods)
{
	vec4 rgbMask = vec4(1.0, 1.0, 1.0, 1.0);
	vec4 red = vec4(poiThemeColor(poiMods, _RedColor.rgb, _RedColorThemeIndex), _RedColor.a);
	vec4 green = vec4(poiThemeColor(poiMods, _GreenColor.rgb, _GreenColorThemeIndex), _GreenColor.a);
	vec4 blue = vec4(poiThemeColor(poiMods, _BlueColor.rgb, _BlueColorThemeIndex), _BlueColor.a);
	vec4 alpha = vec4(poiThemeColor(poiMods, _AlphaColor.rgb, _AlphaColorThemeIndex), _AlphaColor.a);

#if defined(PROP_RGBMASK)
	if (_RGBMaskType == 0.0)
	{
		rgbMask = poiTexPan(_RGBMask, poiUV(poiMesh.uv[int(_RGBMaskUV)], _RGBMask_ST), _RGBMaskPan);
	}
#endif

	if (_RGBMaskType == 1.0)
	{
		rgbMask = poiMesh.vertexColor;
	}

#if defined(PROP_REDTEXTURE)
	red *= poiTexPanStochastic(_RedTexture, poiUV(poiMesh.uv[int(_RedTextureUV)], PoiUVMerge(_RedTexture_ST, _RGBARedScaleOffset)), _RedTexturePan.xy, _RedTextureStochastic);
#endif
#if defined(PROP_GREENTEXTURE)
	green *= poiTexPanStochastic(_GreenTexture, poiUV(poiMesh.uv[int(_GreenTextureUV)], PoiUVMerge(_GreenTexture_ST, _RGBAGreenScaleOffset)), _GreenTexturePan.xy, _GreenTextureStochastic);
#endif
#if defined(PROP_BLUETEXTURE)
	blue *= poiTexPanStochastic(_BlueTexture, poiUV(poiMesh.uv[int(_BlueTextureUV)], PoiUVMerge(_BlueTexture_ST, _RGBABlueScaleOffset)), _BlueTexturePan.xy, _BlueTextureStochastic);
#endif
#if defined(PROP_ALPHATEXTURE)
	alpha *= poiTexPanStochastic(_AlphaTexture, poiUV(poiMesh.uv[int(_AlphaTextureUV)], PoiUVMerge(_AlphaTexture_ST, _RGBAAlphaScaleOffset)), _AlphaTexturePan.xy, _AlphaTextureStochastic);
#endif

	RGBABlendColor(poiFragData, globalMaskBlend(rgbMask[int(_RgbRedMaskChannel)], _RgbRedGlobalMaskChannel, _RgbRedGlobalMaskBlendType, poiMods), red, _RGBARedEmissionStrength, _RGBARedBlendType, _RedAlphaAdd, _RGBARedEnable);
	RGBABlendColor(poiFragData, globalMaskBlend(rgbMask[int(_RgbGreenMaskChannel)], _RgbGreenGlobalMaskChannel, _RgbGreenGlobalMaskBlendType, poiMods), green, _RGBAGreenEmissionStrength, _RGBAGreenBlendType, _GreenAlphaAdd, _RGBAGreenEnable);
	RGBABlendColor(poiFragData, globalMaskBlend(rgbMask[int(_RgbBlueMaskChannel)], _RgbBlueGlobalMaskChannel, _RgbBlueGlobalMaskBlendType, poiMods), blue, _RGBABlueEmissionStrength, _RGBABlueBlendType, _BlueAlphaAdd, _RGBABlueEnable);
	RGBABlendColor(poiFragData, globalMaskBlend(rgbMask[int(_RgbAlphaMaskChannel)], _RgbAlphaGlobalMaskChannel, _RgbAlphaGlobalMaskBlendType, poiMods), alpha, _RGBAAlphaEmissionStrength, _RGBAAlphaBlendType, _AlphaAlphaAdd, _RGBAAlphaEnable);
}
#endif
