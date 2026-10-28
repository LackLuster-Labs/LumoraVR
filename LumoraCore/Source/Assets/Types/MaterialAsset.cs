// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

public class MaterialAsset : DynamicImplementableAsset<IMaterialAssetHook>
{
    private int _activeRequestCount;
    private MaterialType _materialType;

    public MaterialType MaterialType => _materialType;

    public override int ActiveRequestCount => _activeRequestCount;

    public void SetMaterialType(MaterialType type)
    {
        _materialType = type;
        Hook?.SetMaterialType(type);
        Version++;
    }

    public void SetBlendMode(BlendMode mode)
    {
        Hook?.SetBlendMode(mode);
        Version++;
    }

    public void SetCulling(Culling culling)
    {
        Hook?.SetCulling(culling);
        Version++;
    }

    public void SetFloat(string property, float value)
    {
        Hook?.SetFloat(property, value);
        Version++;
    }

    public void SetInt(string property, int value)
    {
        Hook?.SetInt(property, value);
        Version++;
    }

    public void SetBool(string property, bool value)
    {
        Hook?.SetBool(property, value);
        Version++;
    }

    // True while a texture that IS assigned has not reached the GPU yet. See MaterialProvider.
    public bool HasTexturesStillArriving => Hook?.HasTexturesStillArriving ?? false;

    public string TexturesStillArriving => Hook?.TexturesStillArriving ?? string.Empty;

    public void SetColor(string property, colorHDR value)
    {
        Hook?.SetColor(property, value);
        Version++;
    }

    public void SetFloat2(string property, float2 value)
    {
        Hook?.SetFloat2(property, value);
        Version++;
    }

    // Set + flush one float2 to the live material immediately (per-frame scroll clip_offset). SetFloat2 only
    // stages the value; the shader param isn't touched until a full ApplyChanges. -xlinka
    public void ApplyFloat2Now(string property, float2 value)
    {
        Hook?.ApplyFloat2Now(property, value);
        Version++;
    }

    // A property already set since the last ApplyChanges, pushed to the live material now and nothing else with it.
    public void ApplyStagedNow(string property)
    {
        Hook?.ApplyStagedNow(property);
    }

    public void SetFloat3(string property, float3 value)
    {
        Hook?.SetFloat3(property, value);
        Version++;
    }

    public void SetFloat4(string property, float4 value)
    {
        Hook?.SetFloat4(property, value);
        Version++;
    }

    public void SetTexture(string property, TextureAsset texture)
    {
        Hook?.SetTexture(property, texture);
        Version++;
    }

    public void SetCustomShader(string shaderPath)
    {
        Hook?.SetCustomShader(shaderPath);
        Version++;
    }

    public void SetCustomShaderSource(string shaderSource)
    {
        Hook?.SetCustomShaderSource(shaderSource);
        Version++;
    }

    public void SetNextPassShaderSource(string shaderSource)
    {
        Hook?.SetNextPassShaderSource(shaderSource);
        Version++;
    }

    public void SetCubemap(string property, CubemapAsset cubemap)
    {
        Hook?.SetCubemap(property, cubemap);
        Version++;
    }

    public void Clear()
    {
        Hook?.Clear();
    }

    public void ApplyChanges(Action callback)
    {
        Hook?.ApplyChanges(callback);
    }

    public object GodotMaterial => (Hook?.GodotMaterial) ?? null!;

    public object SharedGodotMaterial => (Hook?.SharedGodotMaterial) ?? null!;

    // -1 = default queue.
    public int RenderQueue => Hook?.RenderQueue ?? -1;

    public bool IsValid => Hook?.IsValid ?? false;

    public void AddRequest()
    {
        _activeRequestCount++;
    }

    public void RemoveRequest()
    {
        _activeRequestCount = System.Math.Max(0, _activeRequestCount - 1);
    }

    public override void Unload()
    {
        _activeRequestCount = 0;
        base.Unload();
    }
}
