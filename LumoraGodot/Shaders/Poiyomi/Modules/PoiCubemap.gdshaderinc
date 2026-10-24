// Ported from Poiyomi Toon Shader 10.0.24 (MIT, Poiyomi Labs); see LICENSE.txt.
// Cubemap. Only the light mask reads the light: applyCubemap blends under the stand-in light of
// PoiMatcap (reaching nothing) and applyCubemapLight adds what each real light uncovers. The cubemap is
// sampled with world directions as they are; its values are linear (DecodeHDR is identity).

#if defined(POI_CUBEMAP)
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

#if defined(PROP_CUBEMAP)
// From Unity's MIT'd Skybox-Cubed.shader
vec3 RotateAroundYInDegrees(vec3 dir, float degrees)
{
	float alpha = degrees * PI / 180.0;
	float sina = sin(alpha);
	float cosa = cos(alpha);
	vec2 rotated = vec2(cosa * dir.x - sina * dir.z, sina * dir.x + cosa * dir.z);
	return vec3(rotated, dir.y).xzy;
}
#endif

// The source's applyCubemap, reading the light it is given.
void poiCubemapApply(inout PoiFragData poiFragData, in PoiCam poiCam, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods)
{
	vec3 CubeMapUV = vec3(0.0);

	switch (int(_CubeMapUVMode))
	{
		case 0: // Skybox
		{
			CubeMapUV = -poiCam.eyeViewDir;
			break;
		}
		case 1: // Reflection
		{
			CubeMapUV = mix(poiCam.vertexReflectionDir, poiCam.reflectionDir, _CubeMapNormal);
			break;
		}
		case 2: // World Normal Direction
		{
			CubeMapUV = mix(poiMesh.normals[0], poiMesh.normals[1], _CubeMapWorldNormalsStrength);
			break;
		}
		case 3: // Local Normal Direction
		{
			CubeMapUV = poiMesh.objNormal;
			break;
		}
	}

#if defined(PROP_CUBEMAP)
	if (any(notEqual(_CubeMapRotation.xyz, vec3(0.0))) || any(notEqual(_CubeMapRotationPan.xyz, vec3(0.0))))
	{
		// Do funny swizzle so we don't have to make a new function for every direction
		CubeMapUV = RotateAroundYInDegrees(CubeMapUV.yxz, _CubeMapRotation.x + (_CubeMapRotationPan.x * POI_TIME.y)).yxz;
		CubeMapUV = RotateAroundYInDegrees(CubeMapUV.xyz, _CubeMapRotation.y + (_CubeMapRotationPan.y * POI_TIME.y)).xyz;
		CubeMapUV = RotateAroundYInDegrees(CubeMapUV.xzy, _CubeMapRotation.z + (_CubeMapRotationPan.z * POI_TIME.y)).xzy;
	}
	float mipLevel = (1.0 - _CubeMapSmoothness) * (1.0 - _CubeMapSmoothness) * 8.0;
	vec4 cubeMap = textureLod(_CubeMap, CubeMapUV, mipLevel);

	cubeMap.rgb *= poiThemeColor(poiMods, _CubeMapColor.rgb, _CubeMapColorThemeIndex);
#else
	vec4 cubeMap = vec4(0.21763764082, 0.21763764082, 0.21763764082, 0.5) * vec4(poiThemeColor(poiMods, _CubeMapColor.rgb, _CubeMapColorThemeIndex), 1.0);
#endif

	cubeMap.rgb *= _CubeMapIntensity;
#if defined(PROP_CUBEMAPMASK)
	float CubeMapMask = poiTexPan(_CubeMapMask, poiUV(poiMesh.uv[int(_CubeMapMaskUV)], _CubeMapMask_ST), _CubeMapMaskPan)[int(_CubeMapMaskChannel)];
#else
	float CubeMapMask = 1.0;
#endif

	if (_CubeMapMaskGlobalMask > 0.0)
	{
		CubeMapMask = maskBlend(CubeMapMask, poiMods.globalMask[int(_CubeMapMaskGlobalMask) - 1], _CubeMapMaskGlobalMaskBlendType);
	}

	if (_CubeMapMaskInvert >= 0.5)
	{
		CubeMapMask = 1.0 - CubeMapMask;
	}

	if (_CubeMapHueShiftEnabled >= 0.5)
	{
		cubeMap.rgb = hueShift(cubeMap.rgb, _CubeMapHueShift + POI_TIME.x * _CubeMapHueShiftSpeed, _CubeMapHueShiftColorSpace, _CubeMapHueSelectOrShift);
		cubeMap = PoiColorBCS(cubeMap, _CubeMapBrightness, _CubeMapContrast, _CubeMapSaturation);
	}
	// The source narrows the ramped light map to its first channel here.
	CubeMapMask = min(CubeMapMask, mix(1.0, poiLight.rampedLightMap.x * poiLight.lightMap, _CubeMapLightMask));
	float cubeMapAlpha = CubeMapMask * cubeMap.a * _CubeMapBlendAmount;
	poiFragData.baseColor.rgb = mix(poiFragData.baseColor.rgb, cubeMap.rgb, cubeMapAlpha * float(_CubemapBlendType == 0.0));
	poiFragData.baseColor.rgb *= mix(vec3(1.0), cubeMap.rgb, cubeMapAlpha * float(_CubemapBlendType == 1.0));
	poiFragData.baseColor.rgb += cubeMap.rgb * cubeMapAlpha * float(_CubemapBlendType == 2.0);
	poiFragData.emission += cubeMap.rgb * _CubeMapEmissionStrength * CubeMapMask * cubeMap.a;
}

void applyCubemap(inout PoiFragData poiFragData, in PoiCam poiCam, in PoiMesh poiMesh, in PoiLight poiLight, in PoiMods poiMods)
{
	PoiLight cubemapLight = poiLight;
	if (poiSurfaceLightDeferred())
	{
		cubemapLight = poiSurfaceBaseLight(poiLight);
	}
	poiCubemapApply(poiFragData, poiCam, poiMesh, cubemapLight, poiMods);
}

// Per light, with Light Mask on: the blend's difference to the stand-in light lands in this light's share;
// the emission difference rides the main-light path only (directional lights), as the source's add pass
// leaves emission out.
#if defined(POI_CUBEMAP_READS_LIGHT)
#define POI_HAS_LIGHT_HOOK_CUBEMAP
#endif
void applyCubemapLight(inout PoiLight poiLight, inout PoiFragData poiFragData, in PoiMesh poiMesh, in PoiCam poiCam, inout PoiMods poiMods)
{
	if (!poiSurfaceLightDeferred() || _CubeMapLightMask == 0.0)
	{
		return;
	}
	PoiCam cubemapCam = poiCam;
	cubemapCam.reflectionDir = reflect(-poiCam.eyeViewDir, poiMesh.normals[1]);
	cubemapCam.vertexReflectionDir = reflect(-poiCam.eyeViewDir, poiMesh.normals[0]);

	PoiFragData litData = poiFragData;
	litData.emission = vec3(0.0);
	PoiFragData baseData = litData;
	poiCubemapApply(litData, cubemapCam, poiMesh, poiLight, poiMods);
	poiCubemapApply(baseData, cubemapCam, poiMesh, poiSurfaceBaseLight(poiLight), poiMods);
	poiLight.finalLightAdd += (litData.baseColor - baseData.baseColor) * poiLight.finalLighting;
	if (poiLight.isAdditive == 0)
	{
		poiLight.finalLightAdd += (litData.emission - baseData.emission) * poiMods.globalEmission;
	}
}
#endif
