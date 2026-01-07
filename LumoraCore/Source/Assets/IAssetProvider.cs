using System;
using System.Collections.Generic;
using Lumora.Core;

namespace Lumora.Core.Assets;

/// <summary>
/// Non-generic interface for asset providers.
/// </summary>
public interface IAssetProvider : IWorldElement
{
    /// <summary>
    /// The generic asset instance (use typed providers for specific asset types).
    /// </summary>
    IAsset GenericAsset { get; }

    /// <summary>
    /// Check if the asset is currently available (loaded).
    /// </summary>
    bool IsAssetAvailable { get; }

    /// <summary>
    /// Number of active references to this asset.
    /// </summary>
    int AssetReferenceCount { get; }

    /// <summary>
    /// All active references to this asset.
    /// </summary>
    IEnumerable<IAssetRef> References { get; }

    /// <summary>
    /// Called when a reference is set to this provider.
    /// </summary>
    void ReferenceSet(IAssetRef reference);

    /// <summary>
    /// Called when a reference is freed from this provider.
    /// </summary>
    void ReferenceFreed(IAssetRef reference);

    /// <summary>
    /// Register a listener for asset update notifications.
    /// </summary>
    void RegisterUpdateListener(IAssetRef reference);

    /// <summary>
    /// Unregister a listener for asset update notifications.
    /// </summary>
    void UnregisterUpdateListener(IAssetRef reference);

    /// <summary>
    /// Internal: Send asset created event to all references.
    /// </summary>
    void SendAssetCreated();

    /// <summary>
    /// Internal: Send asset updated event to listeners.
    /// </summary>
    void SendAssetUpdated();

    /// <summary>
    /// Internal: Send asset removed event to all references.
    /// </summary>
    void SendAssetRemoved();
}

/// <summary>
/// Generic interface for typed asset providers.
/// </summary>
public interface IAssetProvider<A> : IAssetProvider where A : Asset
{
    /// <summary>
    /// The typed asset instance.
    /// </summary>
    A Asset { get; }
}

/// <summary>
/// Interface for asset references (like AssetRef<T>).
/// </summary>
public interface IAssetRef
{
    /// <summary>
    /// The asset provider this reference points to.
    /// </summary>
    IAssetProvider Target { get; set; }

    /// <summary>
    /// Called when the referenced asset is updated.
    /// </summary>
    void AssetUpdated();
}
