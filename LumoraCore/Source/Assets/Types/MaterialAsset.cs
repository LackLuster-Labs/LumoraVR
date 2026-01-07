using System;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Asset containing material/shader data.
/// Bridges to Godot materials via IMaterialAssetHook.
/// Similar to TextureAsset but for materials.
/// </summary>
public class MaterialAsset : DynamicImplementableAsset<IMaterialAssetHook>
{
    private int _activeRequestCount;
    private MaterialType _materialType;

    /// <summary>
    /// The material type (PBS_Metallic, Unlit, Custom).
    /// </summary>
    public MaterialType MaterialType => _materialType;

    public override int ActiveRequestCount => _activeRequestCount;

    /// <summary>
    /// Set the material type (PBS_Metallic, Unlit, etc.).
    /// </summary>
    public void SetMaterialType(MaterialType type)
    {
        _materialType = type;
        Hook?.SetMaterialType(type);
        Version++;
    }

    /// <summary>
    /// Set blend mode.
    /// </summary>
    public void SetBlendMode(BlendMode mode)
    {
        Hook?.SetBlendMode(mode);
        Version++;
    }

    /// <summary>
    /// Set culling mode.
    /// </summary>
    public void SetCulling(Culling culling)
    {
        Hook?.SetCulling(culling);
        Version++;
    }

    /// <summary>
    /// Set a float property.
    /// </summary>
    public void SetFloat(string property, float value)
    {
        Hook?.SetFloat(property, value);
        Version++;
    }

    /// <summary>
    /// Set an int property.
    /// </summary>
    public void SetInt(string property, int value)
    {
        Hook?.SetInt(property, value);
        Version++;
    }

    /// <summary>
    /// Set a bool property.
    /// </summary>
    public void SetBool(string property, bool value)
    {
        Hook?.SetBool(property, value);
        Version++;
    }

    /// <summary>
    /// Set a color property.
    /// </summary>
    public void SetColor(string property, colorHDR value)
    {
        Hook?.SetColor(property, value);
        Version++;
    }

    /// <summary>
    /// Set a float2 property (e.g., texture scale/offset).
    /// </summary>
    public void SetFloat2(string property, float2 value)
    {
        Hook?.SetFloat2(property, value);
        Version++;
    }

    /// <summary>
    /// Set a float3 property.
    /// </summary>
    public void SetFloat3(string property, float3 value)
    {
        Hook?.SetFloat3(property, value);
        Version++;
    }

    /// <summary>
    /// Set a float4 property.
    /// </summary>
    public void SetFloat4(string property, float4 value)
    {
        Hook?.SetFloat4(property, value);
        Version++;
    }

    /// <summary>
    /// Set a texture property via TextureAsset.
    /// </summary>
    public void SetTexture(string property, TextureAsset texture)
    {
        Hook?.SetTexture(property, texture);
        Version++;
    }

    /// <summary>
    /// Set a custom shader path (for Custom material type).
    /// </summary>
    public void SetCustomShader(string shaderPath)
    {
        Hook?.SetCustomShader(shaderPath);
        Version++;
    }

    /// <summary>
    /// Set a custom shader source code string.
    /// </summary>
    public void SetCustomShaderSource(string shaderSource)
    {
        Hook?.SetCustomShaderSource(shaderSource);
        Version++;
    }

    /// <summary>
    /// Clear all properties.
    /// </summary>
    public void Clear()
    {
        Hook?.Clear();
    }

    /// <summary>
    /// Apply changes asynchronously.
    /// </summary>
    public void ApplyChanges(Action callback)
    {
        Hook?.ApplyChanges(callback);
    }

    /// <summary>
    /// Get the underlying Godot material object.
    /// </summary>
    public object GodotMaterial => Hook?.GodotMaterial;

    /// <summary>
    /// Whether the material is valid and ready for use.
    /// </summary>
    public bool IsValid => Hook?.IsValid ?? false;

    /// <summary>
    /// Add an active request for this material.
    /// </summary>
    public void AddRequest()
    {
        _activeRequestCount++;
    }

    /// <summary>
    /// Remove an active request for this material.
    /// </summary>
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
