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
/// Placeholder texture interface (will be implemented in texture system).
/// </summary>
public interface ITexture
{
    // Will be defined in texture asset system
}
