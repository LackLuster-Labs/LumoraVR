using System;
using System.Collections.Generic;
using System.IO;
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
    private Action _sendAssetCreatedDelegate;
    private Action _sendAssetUpdatedDelegate;
    private Action _sendAssetRemovedDelegate;

    // ===== PROPERTIES =====

    public int AssetReferenceCount => references.Count;
    public abstract A Asset { get; }
    public IAsset GenericAsset => Asset;
    public abstract bool IsAssetAvailable { get; }
    public IEnumerable<IAssetRef> References => references;

    // ===== REFERENCE MANAGEMENT =====

    public void ReferenceSet(IAssetRef reference)
    {
        if (IsDestroyed) return;

        AquaLogger.Debug($"AssetProvider.ReferenceSet: [{GetType().Name}] Adding reference, count before: {references.Count}");
        if (references.Add(reference) && references.Count == 1)
        {
            // First reference added - trigger update
            AquaLogger.Debug($"AssetProvider.ReferenceSet: [{GetType().Name}] First reference added, triggering RefreshAssetState");
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
        base.OnDestroy();
    }

    /// <summary>
    /// Called when component changes are applied.
    /// Refreshes asset state based on current reference count.
    /// </summary>
    public override void OnChanges()
    {
        AquaLogger.Debug($"AssetProvider.OnChanges: [{GetType().Name}] Called, refCount={AssetReferenceCount}");
        RefreshAssetState();
    }

    // ===== ASSET STATE MANAGEMENT =====

    private void RefreshAssetState()
    {
        if (AssetReferenceCount == 0 && IsAssetAvailable)
        {
            FreeAsset();
        }
        else if (AssetReferenceCount > 0)
        {
            UpdateAsset();
        }
    }

    protected abstract void FreeAsset();
    protected abstract void UpdateAsset();

    // ===== ASSET EVENTS =====

    protected void AssetCreated()
    {
        if (IsDestroyed || references.Count == 0) return;
        DispatchNotification(GetSendAssetCreatedDelegate());
    }

    protected void AssetUpdated()
    {
        if (IsDestroyed) return;
        if (references.Count == 0 && (updatedListeners == null || updatedListeners.Count == 0))
            return;
        DispatchNotification(GetSendAssetUpdatedDelegate());
    }

    protected void AssetRemoved()
    {
        if (IsDestroyed || references.Count == 0) return;
        DispatchNotification(GetSendAssetRemovedDelegate());
    }

    private void DispatchNotification(Action action)
    {
        var world = World;
        if (world != null)
        {
            world.RunSynchronously(action);
        }
        else
        {
            action();
        }
    }

    private Action GetSendAssetCreatedDelegate()
    {
        if (_sendAssetCreatedDelegate == null)
            _sendAssetCreatedDelegate = ((IAssetProvider)this).SendAssetCreated;
        return _sendAssetCreatedDelegate;
    }

    private Action GetSendAssetUpdatedDelegate()
    {
        if (_sendAssetUpdatedDelegate == null)
            _sendAssetUpdatedDelegate = ((IAssetProvider)this).SendAssetUpdated;
        return _sendAssetUpdatedDelegate;
    }

    private Action GetSendAssetRemovedDelegate()
    {
        if (_sendAssetRemovedDelegate == null)
            _sendAssetRemovedDelegate = ((IAssetProvider)this).SendAssetRemoved;
        return _sendAssetRemovedDelegate;
    }

    void IAssetProvider.SendAssetCreated()
    {
        AquaLogger.Debug($"AssetProvider.SendAssetCreated: [{GetType().Name}] IsDestroyed={IsDestroyed}, refCount={references?.Count ?? 0}");
        if (IsDestroyed) return;

        foreach (IAssetRef reference in references)
        {
            AquaLogger.Debug($"AssetProvider.SendAssetCreated: [{GetType().Name}] Notifying reference {reference.GetType().Name}");
            reference.AssetUpdated();
        }
    }

    void IAssetProvider.SendAssetUpdated()
    {
        if (IsDestroyed) return;

        foreach (IAssetRef reference in references)
        {
            reference.AssetUpdated();
        }

        if (updatedListeners == null)
            return;

        foreach (IAssetRef updatedListener in updatedListeners)
        {
            if (references.Contains(updatedListener))
                continue;
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

        if (updatedListeners != null)
        {
            foreach (IAssetRef updatedListener in updatedListeners)
            {
                if (references.Contains(updatedListener))
                    continue;
                updatedListener.AssetUpdated();
            }
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
            AquaLogger.Debug("AssetProvider.ProcessURL: URL is null");
            return null;
        }

        AquaLogger.Debug($"AssetProvider.ProcessURL: Processing {assetURL} (scheme: {assetURL.Scheme})");

        if (assetURL.Scheme == "lumdb")
        {
            var filename = GetUriRelativePath(assetURL);
            if (string.IsNullOrEmpty(filename))
            {
                AquaLogger.Warn($"AssetProvider: Invalid lumdb URI: {assetURL}");
                return null;
            }
            assetURL = new Uri($"lumora:///{filename}");
        }

        if (assetURL.Scheme == "lumora")
        {
            var filename = GetUriRelativePath(assetURL);
            if (!string.IsNullOrEmpty(filename) && assetURL.AbsolutePath == "/")
            {
                assetURL = new Uri($"lumora:///{filename}");
            }
            return assetURL;
        }

        if (assetURL.Scheme is "lumres" or "res")
        {
            var resourceRoot = Engine.Current?.ResourceRoot;
            if (string.IsNullOrWhiteSpace(resourceRoot))
            {
                AquaLogger.Warn($"AssetProvider: Resource root not set; cannot resolve {assetURL}");
                return null;
            }

            var relativePath = GetUriRelativePath(assetURL);
            if (string.IsNullOrEmpty(relativePath))
            {
                AquaLogger.Warn($"AssetProvider: Invalid resource URI: {assetURL}");
                return null;
            }

            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(resourceRoot, relativePath);
            if (!File.Exists(filePath))
            {
                AquaLogger.Warn($"AssetProvider: Resource not found at '{filePath}' for {assetURL}");
            }
            return new Uri(filePath);
        }

        // Resolve local:// URIs via LocalDB
        if (assetURL.Scheme == "local")
        {
            var localDB = Engine.Current?.LocalDB;
            AquaLogger.Debug($"AssetProvider.ProcessURL: LocalDB is {(localDB != null ? "available" : "NULL")}");
            if (localDB != null)
            {
                var filePath = localDB.GetFilePath(assetURL.ToString());
                AquaLogger.Debug($"AssetProvider.ProcessURL: GetFilePath returned '{filePath}'");
                if (!string.IsNullOrEmpty(filePath))
                {
                    var resolvedUri = new Uri(filePath);
                    AquaLogger.Debug($"AssetProvider.ProcessURL: Resolved to {resolvedUri}");
                    return resolvedUri;
                }
            }
            AquaLogger.Warn($"AssetProvider: Could not resolve local URI: {assetURL}");
            return null;
        }

        return assetURL;
    }

    private static string GetUriRelativePath(Uri uri)
    {
        var path = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path))
        {
            return uri.Host;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return path;
        }

        return $"{uri.Host}/{path}";
    }
}
