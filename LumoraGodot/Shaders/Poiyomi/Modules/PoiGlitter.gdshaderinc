// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// applyGlitter runs in fragment() with poiGlowUnlitLight; applyGlitterLight runs the glitter again per
// light, with and without the light, and adds the difference (PoiEmission.gdshaderinc). -xlinka

#if defined(POI_GLITTER)
struct PoiGlitterData
{
	vec3 norm;
	float jaggyFix;
	float glitterFwidth;
	float glitterSpeedOffset;
	// _GlitterSpeed + glitterSpeedOffset
	float glitterTotalSpeed;
	float glitterRotationTimeOffset;
	bool needsRotation;
	float glitterMask;
	float glitterMaxBrightness;
	float glitterBrightnessOffset;
	// poiThemeColor(_GlitterColor.rgb, _GlitterColorThemeIndex)
	vec3 baseColor;
	// _GlitterColorMap.rgb
	vec3 colorMap;
	// mix(forwardDir, viewDir, _GlitterLinearVRParallax)
	vec3 linearCamDir;
	// normalize(viewDir + lightDir * _GlitterLinearLightDirection)
	vec3 linearHalfDir;
	// (POI_TIME.x + glitterSpeedOffset) * _GlitterLinearSpeed
	float linearTimeFactor;
	// normalize(lightDir + viewDir)
	vec3 lightHalfDir;
};

PoiGlitterData PoiGlitterData_zero()
{
	PoiGlitterData r;
	r.norm = vec3(0.0);
	r.jaggyFix = 0.0;
	r.glitterFwidth = 0.0;
	r.glitterSpeedOffset = 0.0;
	r.glitterTotalSpeed = 0.0;
	r.glitterRotationTimeOffset = 0.0;
	r.needsRotation = false;
	r.glitterMask = 0.0;
	r.glitterMaxBrightness = 0.0;
	r.glitterBrightnessOffset = 0.0;
	r.baseColor = vec3(0.0);
	r.colorMap = vec3(0.0);
	r.linearCamDir = vec3(0.0);
	r.linearHalfDir = vec3(0.0);
	r.linearTimeFactor = 0.0;
	r.lightHalfDir = vec3(0.0);
	return r;
}

vec3 RandomColorFromPoint(vec2 rando, float hue)
{
	float saturation = mix(_GlitterMinMaxSaturation.x, _GlitterMinMaxSaturation.y, rando.x);
	float value = mix(_GlitterMinMaxBrightness.x, _GlitterMinMaxBrightness.y, rando.y);
	return HSVtoRGB(vec3(hue, saturation, value));
}

vec2 glitterComputeRotation(vec2 pos, vec2 center, vec2 randomSeed, float timeOffset)
{
	float randomBoy = poiRand(randomSeed);
	float rotSpeed = mix(_GlitterRandomRotationSpeed.x, _GlitterRandomRotationSpeed.y, randomBoy);
	float randomOffset = _GlitterRandomRotation != 0.0 ? randomBoy : 0.0;
	float totalSpeed = _GlitterTextureRotation + rotSpeed;
	if (totalSpeed == 0.0 && timeOffset != 0.0)
		totalSpeed = 1.0;
	if (_GlitterRandomRotationDirection != 0.0 && randomBoy < 0.5)
		totalSpeed = -totalSpeed;
	float theta = radians(randomOffset * 360.0 + (POI_TIME.x + timeOffset) * totalSpeed * 360.0);
	float sn = sin(theta);
	float cs = cos(theta);
	return vec2((pos.x - center.x) * cs - (pos.y - center.y) * sn + center.x,
		(pos.x - center.x) * sn + (pos.y - center.y) * cs + center.y);
}

void processGlitterParticle(
	inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods,
	in PoiGlitterData poiGlitterData,
	vec2 randPoint, vec2 diff)
{
	float size = _GlitterSize;
	if (_GlitterRandomSize >= 0.5)
		size = mix(_GlitterMinMaxSize.x, _GlitterMinMaxSize.y, poiRand(randPoint));
	size = clamp(size, 0.0, 1.0);

	vec2 rotatedDiff = diff;
	float glitterAlpha = 1.0;
	switch (int(_GlitterShape))
	{
		case 0:
		{
			if (_GlitterMode == 1.0)
			{
				float sqDist = dot(diff, diff);
				glitterAlpha = clamp((size - sqDist) / clamp(fwidth(sqDist), 0.0001, 1.0), 0.0, 1.0);
			}
			else
			{
				float dist = length(diff);
				glitterAlpha = clamp((size - dist) / poiGlitterData.glitterFwidth, 0.0, 1.0);
			}
			break;
		}
		case 1:
		{
			if (poiGlitterData.needsRotation)
				rotatedDiff = glitterComputeRotation(diff, vec2(0.0), randPoint * 200.0, poiGlitterData.glitterRotationTimeOffset);
			float squareAA = max(poiGlitterData.glitterFwidth, 0.1 * poiGlitterData.jaggyFix);
			glitterAlpha = (1.0 - smoothstep(size - squareAA, size, abs(rotatedDiff.x))) * (1.0 - smoothstep(size - squareAA, size, abs(rotatedDiff.y)));
			break;
		}
	}

	if (glitterAlpha <= 0.0) return;

	float finalGlitter = 0.0;
	vec3 glitterColor = poiGlitterData.baseColor;
	vec3 randomRotation = vec3(0.0);

	switch (int(_GlitterMode))
	{
		case 0:
		{
			if (poiGlitterData.glitterTotalSpeed > 0.0)
				randomRotation = randomFloat3WiggleRange(randPoint, _GlitterAngleRange, _GlitterSpeed, poiGlitterData.glitterSpeedOffset);
			else
				randomRotation = poiRand3Range(randPoint, _GlitterAngleRange);
			vec3 glitterReflectionDirection = normalize(mat3(poiRotationMatrixFromAngles(randomRotation)) * poiGlitterData.norm);
			finalGlitter = mix(0.0, _GlitterMinBrightness * glitterAlpha, glitterAlpha) + max(pow(clamp(dot(mix(glitterReflectionDirection, poiCam.eyeViewDir, _GlitterBias), poiCam.eyeViewDir), 0.0, 1.0), _GlitterContrast), 0.0);
			finalGlitter *= glitterAlpha;
			break;
		}
		case 1:
		{
			vec3 cellRand3 = vec3(poiRand(randPoint), poiRand(randPoint + 0.5), poiRand(randPoint.yx));
			vec3 glitterNorm = abs(fract(cellRand3 * 14.274 + poiGlitterData.linearTimeFactor) * 2.0 - 1.0);
			glitterNorm = normalize(glitterNorm * 2.0 - 1.0);
			float glitterDot = dot(glitterNorm, poiGlitterData.linearCamDir);
			float glitterVal = abs(fract(glitterDot * _GlitterLinearSensitivity + _GlitterLinearSensitivity) - 0.5) * 4.0 - 1.0;
			glitterVal = clamp(1.0 - (glitterVal * _GlitterLinearContrast + _GlitterLinearContrast), 0.0, 1.0);
			glitterVal = pow(glitterVal, _GlitterLinearPostContrast);
			float linearNDotH = clamp(dot(poiGlitterData.norm, poiGlitterData.linearHalfDir), 0.0, 1.0);
			glitterVal = clamp(glitterVal * clamp(linearNDotH * _GlitterLinearAngle + 1.0 - _GlitterLinearAngle, 0.0, 1.0), 0.0, 1.0);
			finalGlitter = max(_GlitterLinearMinBrightness, glitterVal) * glitterAlpha;
			break;
		}
		case 2:
		{
			if (poiGlitterData.glitterTotalSpeed > 0.0)
				randomRotation = randomFloat3WiggleRange(randPoint, _GlitterAngleRange, _GlitterSpeed, poiGlitterData.glitterSpeedOffset);
			else
				randomRotation = poiRand3Range(randPoint, _GlitterAngleRange);
			vec3 glitterLightReflectionDirection = normalize(mat3(poiRotationMatrixFromAngles(randomRotation)) * poiGlitterData.norm);
			float lightAlpha = glitterAlpha * poiLight.nDotLSaturated;
			float specAngle = max(dot(poiGlitterData.lightHalfDir, glitterLightReflectionDirection), 0.0);
			finalGlitter = mix(0.0, _GlitterMinBrightness * lightAlpha, lightAlpha) + max(pow(specAngle, _GlitterContrast), 0.0);
			glitterColor *= poiLight.directColor;
			finalGlitter *= lightAlpha;
			break;
		}
	}

	glitterColor *= mix(vec3(1.0), poiFragData.baseColor, _GlitterUseSurfaceColor);
	glitterColor *= poiGlitterData.colorMap;

	vec2 uv = remapClamped(vec2(-size), vec2(size), rotatedDiff, vec2(0.0), vec2(1.0));
	if (_GlitterShape == 0.0 && poiGlitterData.needsRotation)
		uv = glitterComputeRotation(uv, vec2(0.5), randPoint * 200.0, poiGlitterData.glitterRotationTimeOffset);

#if defined(PROP_GLITTERTEXTURE)
	vec2 glitterTexUV = uv;
	if (_GlitterSpriteSheetEnabled >= 0.5)
	{
		float glitterSeed = poiRand(randPoint);
		float frameMode = _GlitterSpriteSheetFrameSelection == 1.0 ? 2.0 : 0.0;
		float glitterFrameIndex = poiSpriteSheetFrameIndex(
			_GlitterSpriteSheetColumns, _GlitterSpriteSheetRows, _GlitterSpriteSheetTotalFrames,
			frameMode, _GlitterSpriteSheetFPS, _GlitterSpriteSheetRandomStart,
			0.0, glitterSeed);
		glitterTexUV = poiSpriteSheetUV(uv, _GlitterSpriteSheetColumns, _GlitterSpriteSheetRows, glitterFrameIndex, 0.0, _GlitterTexture_ST.xy);
	}
	vec2 finalGlitterTexUV = poiUV(glitterTexUV, _GlitterTexture_ST);
	if (_GlitterTexturePixelMode >= 0.5)
		finalGlitterTexUV = sharpSample(poiTexelSize(_GlitterTexture), finalGlitterTexUV);
	vec4 glitterTexture = poiTexPanGrad(_GlitterTexture, finalGlitterTexUV, vec2(0.0), poiMesh.dx, poiMesh.dy);
#else
	vec4 glitterTexture = vec4(1.0);
#endif
	glitterColor *= glitterTexture.rgb;

	if (_GlitterRandomColors >= 0.5 || _GlitterHueShiftEnabled >= 0.5)
	{
		vec2 colorRando = poiRand2(randPoint.x + randPoint.y);
		float randomHue = poiRand(colorRando);
		if (_GlitterRandomColors >= 0.5)
			glitterColor *= RandomColorFromPoint(colorRando, randomHue);
		if (_GlitterHueShiftEnabled >= 0.5)
		{
			float hueOffset = _GlitterHueShift + POI_TIME.x * _GlitterHueShiftSpeed;
			if (_GlitterHueShiftRandomOffset >= 0.5)
				hueOffset += randomHue;
			glitterColor.rgb = hueShift(glitterColor.rgb, hueOffset, _GlitterHueShiftColorSpace, _GlitterHueSelectOrShift);
		}
	}

	float brightness = poiGlitterData.glitterMaxBrightness + poiGlitterData.glitterBrightnessOffset;
	float maskedAlpha = finalGlitter * glitterTexture.a * poiGlitterData.glitterMask;
	if (_GlitterBlendType == 1.0)
	{
		poiFragData.baseColor = mix(poiFragData.baseColor, finalGlitter * glitterColor * brightness, maskedAlpha);
		poiFragData.emission += finalGlitter * glitterColor * max(0.0, (brightness - 1.0) * glitterTexture.a) * poiGlitterData.glitterMask;
	}
	else
	{
		poiFragData.emission += glitterColor * brightness * maskedAlpha;
	}
}

// applyGlitter with the light state to read.
void poiApplyGlitter(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods)
{
	PoiGlitterData poiGlitterData;
	PoiInitStruct(PoiGlitterData, poiGlitterData)

#if defined(PROP_GLITTERMASK)
	float glitterMask = poiTexPan(_GlitterMask, poiUV(poiMesh.uv[int(_GlitterMaskUV)], _GlitterMask_ST), _GlitterMaskPan)[int(_GlitterMaskChannel)];
#else
	float glitterMask = 1.0;
#endif
	if (_GlitterMaskInvert >= 0.5)
		glitterMask = 1.0 - glitterMask;
	glitterMask *= mix(1.0, poiLight.rampedLightMap.x * poiLight.lightMap, _GlitterHideInShadow);
	glitterMask *= mix(1.0, poiLight.directLuminance, _GlitterScaleWithLighting);
	glitterMask *= _GlitterColor.a;
	if (_GlitterMaskGlobalMask > 0.0)
		glitterMask = maskBlend(glitterMask, poiMods.globalMask[int(_GlitterMaskGlobalMask) - 1], _GlitterMaskGlobalMaskBlendType);

	if (glitterMask <= 0.0) return;
	poiGlitterData.glitterMask = glitterMask;

	poiGlitterData.glitterBrightnessOffset = 0.0;
	poiGlitterData.glitterMaxBrightness = _GlitterMode == 1.0 ? _GlitterLinearBrightness : _GlitterBrightness;
	poiGlitterData.needsRotation = _GlitterRandomRotation != 0.0 || _GlitterTextureRotation != 0.0 || any(notEqual(_GlitterRandomRotationSpeed.xy, vec2(0.0))) || poiGlitterData.glitterRotationTimeOffset != 0.0;
	poiGlitterData.norm = mix(poiMesh.normals[0], poiMesh.normals[1], _GlitterUseNormals);
	poiGlitterData.jaggyFix = poiCam.distanceToVert * poiCam.distanceToVert * _GlitterJaggyFix;
	poiGlitterData.glitterTotalSpeed = _GlitterSpeed + poiGlitterData.glitterSpeedOffset;

	if (_GlitterMode == 1.0)
	{
		poiGlitterData.linearCamDir = mix(poiCam.forwardDir, poiCam.eyeViewDir, _GlitterLinearVRParallax);
		poiGlitterData.linearHalfDir = normalize(poiGlitterData.linearCamDir + poiLight.direction * _GlitterLinearLightDirection);
		poiGlitterData.linearTimeFactor = (POI_TIME.x + poiGlitterData.glitterSpeedOffset) * _GlitterLinearSpeed;
	}
	else if (_GlitterMode == 2.0)
	{
		poiGlitterData.lightHalfDir = poiLight.halfDir;
	}

	poiGlitterData.baseColor = poiThemeColor(poiMods, _GlitterColor.rgb, _GlitterColorThemeIndex);
	poiGlitterData.colorMap = vec3(1.0);
#if defined(PROP_GLITTERCOLORMAP)
	poiGlitterData.colorMap = poiTexPanGrad(_GlitterColorMap, poiUV(poiMesh.uv[int(_GlitterColorMapUV)], _GlitterColorMap_ST), _GlitterColorMapPan, poiMesh.dx, poiMesh.dy).rgb;
#endif

	float maxSize = _GlitterRandomSize != 0.0 ? _GlitterMinMaxSize.y : _GlitterSize;
	maxSize = clamp(maxSize, 0.0, 1.0);

	float maxParticleDistSq = _GlitterShape == 1.0
		? 2.0 * maxSize * maxSize
		: (_GlitterMode == 1.0 ? maxSize : maxSize * maxSize);

	vec2 st = (poiMesh.uv[int(_GlitterUV)] + _GlitterUVPanning.xy * POI_TIME.x) * _GlitterFrequency;
	vec2 dd = fwidth(st);
	float distanceLODEnabled = _GlitterMode == 1.0 ? _GlitterLinearDistanceLOD : _GlitterDistanceLOD;
	poiGlitterData.glitterFwidth = clamp(length(dd), 0.0001, 1.0);
	if (distanceLODEnabled != 0.0)
	{
		float lodFactor = fract(sin(dot(floor(st / floor(dd + 3.0)), vec2(12.9898, 78.233))) * 46203.4357) + 0.5;
		vec2 lodScale = floor(dd + lodFactor * 0.5);
		st = st / max(vec2(1.0), lodScale) + _GlitterFrequency * lodScale;
	}

	vec2 i_st = floor(st);
	vec2 f_st = fract(st);

	uint glitterLayerCount = min(uint(_GlitterLayers), 4u);
	for (uint glitterLayer = 0u; glitterLayer < glitterLayerCount; glitterLayer++)
	{
		vec2 neighborhoodSeed = i_st + float(glitterLayer) * 0.5141;
		vec2 neighborhoodOrigin = -f_st;

		if (_GlitterFullCoverage >= 0.5)
		{
			// Process every cell whose particle could overlap this fragment.
			for (int poiCell0 = 0; poiCell0 < 9; poiCell0++)
			{
				vec2 neighbor = vec2(float(poiCell0 % 3 - 1), float(poiCell0 / 3 - 1));
				vec2 cellRando = poiRand2(neighborhoodSeed + neighbor);
				vec2 cellDiff = neighborhoodOrigin + neighbor + cellRando * _GlitterRandomLocation;
				if (dot(cellDiff, cellDiff) >= maxParticleDistSq) continue;
				processGlitterParticle(poiFragData, poiMesh, poiCam, poiLight, poiMods, poiGlitterData, cellRando, cellDiff);
			}
		}
		else
		{
			// Default: find the closest cell and process its particle only.
			float m_distSq = 100.0;
			vec2 randoPoint = vec2(0.0);
			vec2 closestDiff = vec2(0.0);

			for (int poiCell1 = 0; poiCell1 < 9; poiCell1++)
			{
				vec2 neighbor = vec2(float(poiCell1 % 3 - 1), float(poiCell1 / 3 - 1));
				vec2 cellRando = poiRand2(neighborhoodSeed + neighbor);
				vec2 cellDiff = neighborhoodOrigin + neighbor + cellRando * _GlitterRandomLocation;
				float cellDistSq = dot(cellDiff, cellDiff);
				if (cellDistSq < m_distSq)
				{
					closestDiff = cellDiff;
					m_distSq = cellDistSq;
					randoPoint = cellRando;
				}
			}

			if (m_distSq < maxParticleDistSq)
				processGlitterParticle(poiFragData, poiMesh, poiCam, poiLight, poiMods, poiGlitterData, randoPoint, closestDiff);
		}
	}
}

void applyGlitter(inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, in PoiLight poiLight, in PoiMods poiMods)
{
	poiApplyGlitter(poiFragData, poiMesh, poiCam, poiGlowUnlitLight(poiLight, poiMesh, poiCam), poiMods);
}

bool poiGlitterUsesLight()
{
	return _GlitterMode == 2.0 || _GlitterHideInShadow != 0.0 || _GlitterScaleWithLighting != 0.0 || (_GlitterMode == 1.0 && _GlitterLinearLightDirection != 0.0);
}

// Light reflection mode, hide in shadow, scale with lighting and the linear mode's light direction. The
// unlit run is skipped where it adds nothing (fully masked, or no light to reflect).
#if defined(POI_GLITTER_READS_LIGHT)
#define POI_HAS_LIGHT_HOOK_GLITTER
#endif
void applyGlitterLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	if (!poiGlitterUsesLight())
	{
		return;
	}
	PoiFragData fragLit = poiFragData;
	PoiFragData fragUnlit = poiFragData;
	fragLit.emission = vec3(0.0);
	fragUnlit.emission = vec3(0.0);
	poiApplyGlitter(fragLit, poiMesh, poiCam, poiLight, poiMods);
	if (!(_GlitterMode == 2.0 || _GlitterHideInShadow == 1.0 || _GlitterScaleWithLighting == 1.0))
	{
		poiApplyGlitter(fragUnlit, poiMesh, poiCam, poiGlowUnlitLight(poiLight, poiMesh, poiCam), poiMods);
	}
	poiGlowAddLightShare(poiLight, fragLit.baseColor - fragUnlit.baseColor, (fragLit.emission - fragUnlit.emission) * poiMods.globalEmission * _PPEmissionMultiplier);
}
#endif
