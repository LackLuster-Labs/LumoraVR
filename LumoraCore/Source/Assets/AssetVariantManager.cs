// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// One per (URL, type). Each distinct variant descriptor gets its own instance and equal descriptors share
// one. Matching is exact; nothing here generates variants.
public abstract class AssetVariantManager
{
    public readonly Uri AssetURL;
    public readonly AssetManager AssetManager;

    // Serializes variant add/remove and unload scheduling for this (URL, type).
    internal readonly object VariantLock = new();

    public Engine Engine => AssetManager.Engine;

    public abstract int RequestCount { get; }
    public abstract Type AssetType { get; }

    protected AssetVariantManager(Uri assetUrl, AssetManager manager)
    {
        AssetURL = assetUrl;
        AssetManager = manager;
    }

    public abstract void RequestAsset(IAssetRequester requester, IAssetVariantDescriptor? descriptor);
    public abstract void RemoveRequest(IAssetRequester requester, IAssetVariantDescriptor? descriptor);

    internal abstract void OnAssetUnloaded(LoadableAsset asset);

    internal abstract bool Forget(IAssetVariantDescriptor? descriptor);

    // Whoever still asks for an instance whose load failed, across every variant.
    internal abstract void CollectFailed(List<IAssetRequester> into);

    // Unloads only if nothing references the asset when the delay ends. A later schedule bumps UnloadKey
    // and supersedes this one, so a request and release in between can't cut the delay short.
    internal void ScheduleUnload(LoadableAsset asset, float seconds)
    {
        asset.UnloadKey++;
        int key = asset.UnloadKey;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
                lock (VariantLock)
                {
                    if (asset.UnloadKey == key && asset.ActiveRequestCount <= 0)
                    {
                        asset.QueueUnload();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"AssetVariantManager: error scheduling unload for {AssetURL}: {ex.Message}");
            }
        });
    }
}

public sealed class AssetVariantManager<A> : AssetVariantManager where A : LoadableAsset, new()
{
    // Key for the "no descriptor" default variant (descriptors can't be null dictionary keys).
    private static readonly object DefaultKey = new();

    private readonly Dictionary<object, A> _variants = new();

    public override Type AssetType => typeof(A);

    public override int RequestCount
    {
        get
        {
            lock (VariantLock)
            {
                int count = 0;
                foreach (var variant in _variants.Values)
                    count += variant.ActiveRequestCount;
                return count;
            }
        }
    }

    public AssetVariantManager(Uri assetUrl, AssetManager manager)
        : base(assetUrl, manager)
    {
    }

    public override void RequestAsset(IAssetRequester requester, IAssetVariantDescriptor? descriptor)
    {
        object key = descriptor ?? DefaultKey;
        lock (VariantLock)
        {
            if (!_variants.TryGetValue(key, out var asset))
            {
                asset = new A { VariantManager = this, TargetVariant = descriptor };
                asset.InitializeStatic(AssetURL, AssetManager);
                _variants[key] = asset;
            }
            asset.TryAddRequest(requester);
        }
    }

    public override void RemoveRequest(IAssetRequester requester, IAssetVariantDescriptor? descriptor)
    {
        object key = descriptor ?? DefaultKey;
        lock (VariantLock)
        {
            if (_variants.TryGetValue(key, out var asset))
            {
                asset.RemoveRequest(requester);
                if (asset.ActiveRequestCount <= 0)
                    ScheduleUnload(asset, asset.UnloadDelay);
            }
        }
    }

    // Takes this variant's instance out of the share while nothing asks for it, so the next request makes a
    // new one and loads it from scratch. The old one goes the usual way, unloaded when its delay runs out, so
    // whatever still draws it this frame is not left holding a freed resource. False while something holds it.
    internal override bool Forget(IAssetVariantDescriptor? descriptor)
    {
        object key = descriptor ?? DefaultKey;
        bool emptied;
        lock (VariantLock)
        {
            if (!_variants.TryGetValue(key, out var asset))
                return true;
            if (asset.ActiveRequestCount > 0)
                return false;
            _variants.Remove(key);
            emptied = _variants.Count == 0;
        }
        if (emptied)
            AssetManager.ScheduleVariantManagerRemoval(new AssetID(AssetURL, typeof(A)));
        return true;
    }

    internal override void CollectFailed(List<IAssetRequester> into)
    {
        lock (VariantLock)
        {
            foreach (var variant in _variants.Values)
            {
                if (variant.LoadState == AssetLoadState.Failed)
                    variant.CollectRequesters(into);
            }
        }
    }

    internal override void OnAssetUnloaded(LoadableAsset asset)
    {
        bool emptied;
        lock (VariantLock)
        {
            object? foundKey = null;
            foreach (var entry in _variants)
            {
                if (entry.Value == asset)
                {
                    foundKey = entry.Key;
                    break;
                }
            }
            if (foundKey == null)
                return;

            _variants.Remove(foundKey);
            emptied = _variants.Count == 0;
        }

        // Schedule manager removal outside VariantLock. ScheduleVariantManagerRemoval takes the
        // manager lock, and AssetManager.Update takes the manager lock then reads RequestCount
        // (VariantLock); keeping these unnested here avoids the lock-order inversion.
        if (emptied)
            AssetManager.ScheduleVariantManagerRemoval(new AssetID(AssetURL, typeof(A)));
    }
}
