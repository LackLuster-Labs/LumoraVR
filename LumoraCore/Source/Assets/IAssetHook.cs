using System;
using Lumora.Core.Math;

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
/// Interface for setting properties on shared materials (Material and MaterialPropertyBlock).
/// </summary>
public interface ISharedMaterialPropertySetter
{
    void SetFloat(int property, float value);
    void SetFloat4(int property, in float4 value);
    void SetFloatArray(int property, System.Collections.Generic.List<float> values);
    void SetFloat4Array(int property, System.Collections.Generic.List<float4> values);
    void SetMatrix(int property, in float4x4 matrix);
    void SetTexture(int property, ITexture texture);
    void SetST(int property, in float2 scale, in float2 offset);
    void SetDebug(bool debug, string tag);
}

/// <summary>
/// Additional material-specific property setters.
/// </summary>
public interface IMaterialPropertySetter
{
    // Extended in derived interfaces as needed
}

/// <summary>
/// Placeholder texture interface (will be implemented in texture system).
/// </summary>
public interface ITexture
{
    // Will be defined in texture asset system
}
