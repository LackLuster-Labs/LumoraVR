using System;
using System.Collections.Generic;
using Lumora.Core;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for components that provide assets to the system.
/// Manages asset lifecycle, reference counting, and update notifications.
/// Bridges between component data (sync fields, URLs) and loaded asset instances.
/// </summary>
public abstract class AssetProvider<A> : Component, IAssetProvider<A> where A : Asset, new()
{
    private HashSet<IAssetRef> references = new HashSet<IAssetRef>();
    private HashSet<IAssetRef> updatedListeners;

    // ===== PROPERTIES =====

    public int AssetReferenceCount => references.Count;
    public abstract A Asset { get; }
    public IAsset GenericAsset => Asset;
    public abstract bool IsAssetAvailable { get; }
    protected virtual bool AlwaysLoad => false;
    protected virtual bool ForceUnload => false;
    public IEnumerable<IAssetRef> References => references;

    // ===== REFERENCE MANAGEMENT =====

    public void ReferenceSet(IAssetRef reference)
    {
        if (IsDestroyed) return;

        if (references.Add(reference) && references.Count == 1)
        {
            // First reference added - trigger update
            RefreshAssetState();
        }
    }

    public void ReferenceFreed(IAssetRef reference)
    {
        if (IsDestroyed) return;

        references.Remove(reference);
        if (references.Count == 0)
        {
            // Last reference removed - may trigger unload
            RefreshAssetState();
        }
    }

    public void RegisterUpdateListener(IAssetRef reference)
    {
        if (IsDestroyed) return;

        if (updatedListeners == null)
        {
            updatedListeners = new HashSet<IAssetRef>();
        }
        updatedListeners.Add(reference);
    }

    public void UnregisterUpdateListener(IAssetRef reference)
    {
        if (IsDestroyed) return;
        updatedListeners?.Remove(reference);
    }

    // ===== LIFECYCLE =====

    public override void OnDestroy()
    {
        FreeAsset();
        AssetRemoved();
        base.OnDestroy();
    }

    // ===== ASSET STATE MANAGEMENT =====

    private void RefreshAssetState()
    {
        if (ForceUnload)
        {
            FreeAsset();
        }
        else if (AlwaysLoad)
        {
            UpdateAsset();
        }
        else if (AssetReferenceCount == 0 && IsAssetAvailable)
        {
            // Schedule delayed unload when no references
            // TODO: Implement delayed task system
            TryFreeAsset();
        }
        else if (AssetReferenceCount > 0)
        {
            UpdateAsset();
        }
    }

    protected void TryFreeAsset()
    {
        if (AssetReferenceCount == 0 && IsAssetAvailable)
        {
            FreeAsset();
        }
    }

    protected abstract void FreeAsset();
    protected abstract void UpdateAsset();

    // ===== ASSET EVENTS =====

    protected void AssetCreated()
    {
        if (IsDestroyed || references.Count == 0) return;
        // Queue for processing on next frame
        ((IAssetProvider)this).SendAssetCreated();
    }

    protected void AssetUpdated()
    {
        if (IsDestroyed || updatedListeners == null || updatedListeners.Count == 0) return;
        // Queue for processing on next frame
        ((IAssetProvider)this).SendAssetUpdated();
    }

    protected void AssetRemoved()
    {
        if (IsDestroyed || references.Count == 0) return;
        // Queue for processing on next frame
        ((IAssetProvider)this).SendAssetRemoved();
    }

    void IAssetProvider.SendAssetCreated()
    {
        if (IsDestroyed) return;

        foreach (IAssetRef reference in references)
        {
            reference.AssetUpdated();
        }
    }

    void IAssetProvider.SendAssetUpdated()
    {
        if (IsDestroyed) return;

        foreach (IAssetRef updatedListener in updatedListeners)
        {
            updatedListener.AssetUpdated();
        }
    }

    void IAssetProvider.SendAssetRemoved()
    {
        if (references == null) return;

        foreach (IAssetRef reference in references)
        {
            reference.AssetUpdated();
        }

        if (IsDestroyed)
        {
            references.Clear();
            references = null;
        }
    }

    protected virtual Uri ProcessURL(Uri assetURL)
    {
        if (assetURL == null)
        {
            return null;
        }
        // TODO: Implement scheme validation when AssetManager exists
        return assetURL;
    }
}
