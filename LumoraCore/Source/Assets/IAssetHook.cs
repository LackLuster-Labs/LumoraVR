using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Base interface for asset hooks that bridge C# assets to engine-specific implementations.
/// </summary>
public interface IAssetHook
{
    /// <summary>
    /// Initialize the hook with an asset instance.
    /// </summary>
    void Initialize(IAsset asset);

    /// <summary>
    /// Unload/dispose the hook and its resources.
    /// </summary>
    void Unload();
}

/// <summary>
/// Hook interface for texture assets.
/// </summary>
public interface ITextureAssetHook : IAssetHook
{
    /// <summary>
    /// Upload texture data to the renderer.
    /// </summary>
    void UploadData(byte[] pixels, int width, int height, bool hasMipmaps);

    /// <summary>
    /// Update texture wrap modes.
    /// </summary>
    void SetWrapMode(TextureWrapMode wrapU, TextureWrapMode wrapV);

    /// <summary>
    /// Whether the texture is valid and can be used.
    /// </summary>
    bool IsValid { get; }
}

/// <summary>
/// Hook interface for mesh data assets.
/// </summary>
public interface IMeshAssetHook : IAssetHook
{
    /// <summary>
    /// Upload mesh data to the renderer.
    /// </summary>
    void UploadMesh(Phos.PhosMesh mesh);

    /// <summary>
    /// Whether the mesh is valid and can be used.
    /// </summary>
    bool IsValid { get; }
}

/// <summary>
/// Hook interface for material assets - bridges to Godot ShaderMaterial/StandardMaterial3D.
/// </summary>
public interface IMaterialAssetHook : IAssetHook
{
    /// <summary>
    /// Set the material type/shader.
    /// </summary>
    void SetMaterialType(MaterialType type);

    /// <summary>
    /// Set blend mode (Opaque, Cutout, Transparent, Additive).
    /// </summary>
    void SetBlendMode(BlendMode mode);

    /// <summary>
    /// Set face culling mode.
    /// </summary>
    void SetCulling(Culling culling);

    /// <summary>
    /// Set a float property.
    /// </summary>
    void SetFloat(string property, float value);

    /// <summary>
    /// Set an int property.
    /// </summary>
    void SetInt(string property, int value);

    /// <summary>
    /// Set a bool property.
    /// </summary>
    void SetBool(string property, bool value);

    /// <summary>
    /// Set a color/float4 property.
    /// </summary>
    void SetColor(string property, Math.colorHDR value);

    /// <summary>
    /// Set a float2 property (e.g., texture scale/offset).
    /// </summary>
    void SetFloat2(string property, Math.float2 value);

    /// <summary>
    /// Set a float3 property.
    /// </summary>
    void SetFloat3(string property, Math.float3 value);

    /// <summary>
    /// Set a float4 property.
    /// </summary>
    void SetFloat4(string property, Math.float4 value);

    /// <summary>
    /// Set a texture property.
    /// </summary>
    void SetTexture(string property, TextureAsset texture);

    /// <summary>
    /// Set a custom shader path (for Custom material type).
    /// </summary>
    void SetCustomShader(string shaderPath);

    /// <summary>
    /// Set a custom shader source code string.
    /// </summary>
    void SetCustomShaderSource(string shaderSource);

    /// <summary>
    /// Clear all properties (called before UpdateMaterial).
    /// </summary>
    void Clear();

    /// <summary>
    /// Apply all pending changes.
    /// </summary>
    void ApplyChanges(Action callback);

    /// <summary>
    /// Get the underlying Godot material for assignment to renderers.
    /// </summary>
    object GodotMaterial { get; }

    /// <summary>
    /// Whether the material is valid and ready for use.
    /// </summary>
    bool IsValid { get; }
}

/// <summary>
/// Texture wrap modes for sampling.
/// </summary>
public enum TextureWrapMode
{
    Repeat,
    Clamp,
    Mirror,
    ClampToBorder
}
