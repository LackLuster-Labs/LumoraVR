using System;
using System.Collections.Generic;

namespace Lumora.Core.Assets;

/// <summary>
/// Delegate for asset integration completion callback.
/// </summary>
public delegate void AssetIntegrated();

/// <summary>
/// Interface for shared material hooks (Material and MaterialPropertyBlock).
/// </summary>
public interface ISharedMaterialHook : IAssetHook, ISharedMaterialPropertySetter
{
    /// <summary>
    /// Initialize shader properties with their IDs.
    /// </summary>
    void InitializeProperties(List<MaterialProperty> properties, Action onDone);
}

/// <summary>
/// Material-specific hook interface.
/// Handles material-specific operations like shader application, render queue, and tags.
/// </summary>
public interface IMaterialHook : ISharedMaterialHook, IMaterialPropertySetter
{
    /// <summary>
    /// Apply changes to the material with a specific shader.
    /// Called when material properties or shader change.
    /// </summary>
    void ApplyChanges(Shader shader, AssetIntegrated onDone);

    /// <summary>
    /// Enable or disable GPU instancing for this material.
    /// </summary>
    void SetInstancing(bool enabled);

    /// <summary>
    /// Set the render queue for this material (controls render order).
    /// </summary>
    void SetRenderQueue(int renderQueue);

    /// <summary>
    /// Set a material tag (e.g., "RenderType" = "Opaque").
    /// </summary>
    void SetTag(MaterialTag tag, string value);
}

/// <summary>
/// MaterialPropertyBlock-specific hook interface.
/// Lighter-weight than materials, used for per-instance property overrides.
/// </summary>
public interface IMaterialPropertyBlockHook : ISharedMaterialHook
{
    /// <summary>
    /// Apply changes to the property block.
    /// </summary>
    void ApplyChanges(AssetIntegrated onDone);
}

/// <summary>
/// Material tag types for shader tags.
/// </summary>
public enum MaterialTag
{
    RenderType,
    Queue,
    IgnoreProjector,
    ForceNoShadowCasting,
    PreviewType
}

/// <summary>
/// Placeholder shader class (will be implemented in shader asset system).
/// </summary>
public class Shader
{
    // Will be defined in shader asset system
    public string Name { get; set; }
}
