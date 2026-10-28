// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.IO;
using Lumora.Core;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

public abstract class AssetProvider<A> : Component, IAssetProvider<A> where A : Asset, new()
{
    private HashSet<IAssetRef> references = new HashSet<IAssetRef>();
    private HashSet<IAssetRef> updatedListeners = null!;
    private Action _sendAssetCreatedDelegate = null!;
    private Action _sendAssetUpdatedDelegate = null!;
    private Action _sendAssetRemovedDelegate = null!;
    private bool _refreshing;
    private bool _refreshQueued;

    // PROPERTIES

    public int AssetReferenceCount => references.Count;
    public abstract A Asset { get; }
    public IAsset GenericAsset => Asset;
    public abstract bool IsAssetAvailable { get; }
    public IEnumerable<IAssetRef> References => references;

    // REFERENCE MANAGEMENT

    public void ReferenceSet(IAssetRef reference)
    {
        if (IsDestroyed) return;

        LumoraLogger.Debug($"AssetProvider.ReferenceSet: [{GetType().Name}] Adding reference, count before: {references.Count}");
        if (references.Add(reference) && references.Count == 1)
        {
            LumoraLogger.Debug($"AssetProvider.ReferenceSet: [{GetType().Name}] First reference added, triggering RefreshAssetState");
            RefreshOnWorldThread();
        }
    }

    public void ReferenceFreed(IAssetRef reference)
    {
        if (IsDestroyed) return;

        references.Remove(reference);
        if (references.Count == 0)
        {
            RefreshOnWorldThread();
        }
    }

    // A reference comes and goes on whatever thread wrote it, and on a guest that is the sync thread,
    // decoding the world it was sent while it holds that world. Building or freeing the asset there runs its
    // hook there too, and a hook calls into the engine: a material loading a shader variant waited on the
    // main thread inside the engine while the main thread waited on the world the sync thread held, and the
    // guest froze for good the moment a big import reached it. Off the world thread the refresh waits for the
    // world's next updates instead, once however many references changed meanwhile. On it, refreshes share a
    // few milliseconds a frame and the rest wait their turn (AssetRefreshQueue): receiving an import is a
    // thousand of them at once. Before a world's first update its thread is not known yet: a thread holding
    // the world then is the sync thread, and anything else is whoever is building it, which gets the asset
    // at once as before. -xlinka
    private void RefreshOnWorldThread()
    {
        var world = World;
        if (world == null || (world.WorldThreadId == 0 && world.HookManager?.HeldByCurrentThread != true))
        {
            RefreshAssetState();
            return;
        }
        if (_refreshQueued)
            return;
        if (world.IsOnWorldThread && AssetRefreshQueue.TryRunNow(world, _refreshNow ??= RefreshAssetState))
            return;
        _refreshQueued = true;
        AssetRefreshQueue.Enqueue(world, _refreshQueuedRun ??= RunQueuedRefresh);
    }

    // Made once: a material changing every frame refreshed through here every frame.
    private Action? _refreshNow;
    private Action? _refreshQueuedRun;

    private void RunQueuedRefresh()
    {
        _refreshQueued = false;
        if (!IsDestroyed)
            RefreshAssetState();
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

    // LIFECYCLE

    public override void OnDestroy()
    {
        FreeAsset();
        base.OnDestroy();
    }

    public override void OnChanges()
    {
        LumoraLogger.Debug($"AssetProvider.OnChanges: [{GetType().Name}] Called, refCount={AssetReferenceCount}");
        RefreshOnWorldThread();
    }

    // ASSET STATE MANAGEMENT

    private void RefreshAssetState()
    {
        // Guards against ApplyChanges re-entering OnChanges via a Sync write-back, which
        // would otherwise spin: OnChanges -> Update -> ApplyChanges -> OnChanges. - xlinka
        if (_refreshing) return;
        _refreshing = true;
        try
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
        finally
        {
            _refreshing = false;
        }
    }

    protected abstract void FreeAsset();
    protected abstract void UpdateAsset();

    // ASSET EVENTS

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

    public void NotifyAssetUpdated() => AssetUpdated();

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
        LumoraLogger.Debug($"AssetProvider.SendAssetCreated: [{GetType().Name}] IsDestroyed={IsDestroyed}, refCount={references?.Count ?? 0}");
        if (IsDestroyed) return;

        foreach (IAssetRef reference in references!)
        {
            LumoraLogger.Debug($"AssetProvider.SendAssetCreated: [{GetType().Name}] Notifying reference {reference.GetType().Name}");
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
            references = null!;
        }
    }

    protected virtual Uri ProcessURL(Uri assetURL)
    {
        if (assetURL == null)
        {
            LumoraLogger.Debug("AssetProvider.ProcessURL: URL is null");
            return null!;
        }

        LumoraLogger.Debug($"AssetProvider.ProcessURL: Processing {assetURL} (scheme: {assetURL.Scheme})");

        if (assetURL.Scheme == "lumdb")
        {
            var filename = GetUriRelativePath(assetURL);
            if (string.IsNullOrEmpty(filename))
            {
                LumoraLogger.Warn($"AssetProvider: Invalid lumdb URI: {assetURL}");
                return null!;
            }
            assetURL = new Uri($"lumora:///{filename}");
        }

        // lumora:///<hash>: a cloud asset. The local database is asked first, keyed by the hash alone
        // (whatever machine id its record carries), because a fetched save brings its assets down
        // there before it loads. Anything not there streams from the content service through the
        // shared download cache. -xlinka
        if (assetURL.Scheme == "lumora")
        {
            var hash = Persistence.CloudAssetPacker.HashOf(assetURL.OriginalString);
            if (string.IsNullOrEmpty(hash))
            {
                var filename = GetUriRelativePath(assetURL);
                if (!string.IsNullOrEmpty(filename) && assetURL.AbsolutePath == "/")
                    assetURL = new Uri($"lumora:///{filename}");
                return assetURL;
            }
            // Held, it is read through the database by its address (the file on disk is sealed), so the
            // address is what goes on, never the file.
            var held = $"local://cloud/{hash}";
            if (Engine.Current?.LocalDB?.Exists(held) == true)
                return new Uri(held);
            // A hash this build shipped as one of its own files is served from the build, never fetched.
            var builtin = BuiltinAssetRegistry.ResUriFor(hash);
            if (builtin != null)
                return ProcessURL(new Uri(builtin));
            return new Uri(Lumora.Nexus.Cloud.Cdn.ServiceConfig.Current.GetContentUrl(hash));
        }

        if (assetURL.Scheme is "lumres" or "res")
        {
            var resourceRoot = Engine.Current?.ResourceRoot;
            if (string.IsNullOrWhiteSpace(resourceRoot))
            {
                LumoraLogger.Warn($"AssetProvider: Resource root not set; cannot resolve {assetURL}");
                return null!;
            }

            // CASE-SAFE: System.Uri lowercases the authority, so new Uri("res://Assets/...").Host == "assets" and
            // GetUriRelativePath would yield "assets/..." - which resolves wrong on a case-sensitive filesystem
            // (the Linux export couldn't find the font). Pull the path straight from the original string so the
            // case is preserved exactly as written. -xlinka
            var relativePath = GetResRelativePathPreservingCase(assetURL);
            if (string.IsNullOrEmpty(relativePath))
            {
                LumoraLogger.Warn($"AssetProvider: Invalid resource URI: {assetURL}");
                return null!;
            }

            var filePath = ResolveResourcePath(resourceRoot, relativePath);
            if (!File.Exists(filePath))
            {
                // Expected in a .pck export - the bytes live in the VFS, not on disk; the asset hook loads the
                // imported resource via ResourceLoader. Debug, not Warn, so it isn't alarming noise. -xlinka
                LumoraLogger.Debug($"AssetProvider: Resource not on disk at '{filePath}' for {assetURL} (loads from pack)");
            }
            return new Uri(filePath);
        }

        if (assetURL.Scheme == "local")
        {
            // Never resolved to its file: the database keeps it sealed, and the gather reads it through the
            // database by this address, decrypting as it reads. -xlinka
            //
            // Having no record for a local:// hash is the NORMAL state of a machine that just joined:
            // every such URI in the world it received names content another machine imported. Returning
            // null here ended the story - the provider reported the asset removed, nobody ever asked for
            // the bytes, and since a gather is the only door into peer transfer the joiner never even
            // asked its peers. That is why a joiner could not load an imported mesh at all.
            //
            // Hand the local:// URI straight through instead. The gather path checks our own cache first
            // and falls through to the session transferer, so the load fails only once the transfer has
            // actually failed. -xlinka
            return assetURL;
        }

        if (assetURL.IsFile && !File.Exists(assetURL.LocalPath))
        {
            if (TryResolveStaleProjectFileUri(assetURL, out var resolvedUri))
            {
                LumoraLogger.Warn($"AssetProvider: Remapped stale file URI '{assetURL}' to '{resolvedUri.LocalPath}'");
                return resolvedUri;
            }
        }

        return assetURL;
    }

    private static string ResolveResourcePath(string resourceRoot, string relativePath)
    {
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var filePath = Path.Combine(resourceRoot, relativePath);
        return ResolveExistingPathCaseInsensitive(filePath) ?? filePath;
    }

    private static bool TryResolveStaleProjectFileUri(Uri uri, out Uri resolvedUri)
    {
        resolvedUri = null!;

        var resourceRoot = Engine.Current?.ResourceRoot;
        if (string.IsNullOrWhiteSpace(resourceRoot))
        {
            return false;
        }

        var candidates = new[]
        {
            uri.LocalPath,
            Uri.UnescapeDataString(uri.AbsolutePath)
        };

        foreach (var candidate in candidates)
        {
            var relativePath = TryGetPathAfterProjectRoot(candidate);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var filePath = ResolveResourcePath(resourceRoot, relativePath);
            if (File.Exists(filePath))
            {
                resolvedUri = new Uri(filePath);
                return true;
            }
        }

        return false;
    }

    private static string TryGetPathAfterProjectRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null!;
        }

        var normalized = path.Replace('\\', '/');
        const string marker = "/LumoraGodot/";
        var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null!;
        }

        return normalized.Substring(index + marker.Length);
    }

    private static string ResolveExistingPathCaseInsensitive(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return null!;
        }

        var current = root;
        var remainder = path.Substring(root.Length);
        var segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var direct = Path.Combine(current, segment);
            if (Directory.Exists(direct) || File.Exists(direct))
            {
                current = direct;
                continue;
            }

            if (!Directory.Exists(current))
            {
                return null!;
            }

            string match = null!;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase))
                {
                    match = entry;
                    break;
                }
            }

            if (match == null)
            {
                return null!;
            }

            current = match;
        }

        return File.Exists(current) ? current : null!;
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

    // Relative path of a res://lumres:// URI with ORIGINAL CASE intact. Uri normalizes the authority to lower
    // case, which silently breaks case-sensitive filesystems (Linux exports), so read the original string. -xlinka
    private static string GetResRelativePathPreservingCase(Uri uri)
    {
        var original = uri.OriginalString;
        if (!string.IsNullOrEmpty(original))
        {
            int schemeIdx = original.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0)
                return original.Substring(schemeIdx + 3).TrimStart('/');
        }
        return GetUriRelativePath(uri);
    }
}

