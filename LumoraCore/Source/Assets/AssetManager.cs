// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumora.Core.Networking;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

public class AssetManager : IDisposable
{
    private bool _disposing;

    // Shared asset instances, deduplicated by (URL, type). Guarded by _managerLock.
    private readonly Dictionary<AssetID, AssetVariantManager> _variantManagers = new();
    private readonly List<AssetID> _managersToRemove = new();
    private readonly object _managerLock = new();

    // Assets reach the engine through here, not the global Engine.Current.
    public Engine Engine { get; }

    // At most one heavy source-format parse (Assimp and friends) anywhere in the process at a time.
    // One parse of a real avatar is seconds of CPU and gigabytes of transient allocation, and three
    // measured concurrently showed ZERO parallel speedup while multiplying the peak until the machine
    // paged and the world thread starved. Serializing them costs nothing in throughput and is the
    // difference between a bounded import and an out-of-memory. Baked .lmesh reads must never take
    // this: they are a buffer copy, and gating them would queue a whole scene load behind one FBX.
    // -xlinka
    internal static readonly System.Threading.SemaphoreSlim SourceParseGate = new(1, 1);

    public AssetManager(Engine engine)
    {
        Engine = engine;
    }

    public Task InitializeAsync()
    {
        Logger.Log("AssetManager: Initialized");
        return Task.CompletedTask;
    }

    // AssetFetcher resolves the scheme (local/file/http/builtin/peer). The completion callback fires on
    // whichever thread pumps ProcessQueue, currently the world update loop.
    public Task<byte[]> RequestGather(Uri assetURL)
    {
        if (assetURL == null)
            return Task.FromResult<byte[]>(null!);

        // RunContinuationsAsynchronously, always. TrySetResult is called from inside the world update,
        // and on a default TCS every awaiter parked on this gather resumes INLINE on the completer's
        // thread - so a batch of mesh or texture decodes would run one after another inside a single
        // world frame. Whether that happens is otherwise decided by whether the completing thread
        // happens to carry a SynchronizationContext, which is not a decision anyone made. Forcing the
        // continuations onto the pool makes the placement ours instead of an accident. -xlinka
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        // OriginalString, not ToString(): System.Uri lowercases the authority, and for a local://
        // asset the authority IS the owning machine's id. The peer transferer resolves an owner by
        // matching that id against the connected users' MachineID, which is case-sensitive, so a
        // lowercased one matches nobody and the asset can only ever be served from our own cache.
        // Every local:// URI in the engine is built from a full string, so OriginalString is exactly
        // what the caller asked for. -xlinka
        AssetFetcher.FetchAsset(assetURL.OriginalString, bytes => tcs.TrySetResult(bytes));
        return tcs.Task;
    }

    // All requesters for the same (URL, type) share one instance. It loads on the first request and
    // stays alive until every requester releases it.
    public void RequestAsset<A>(Uri assetURL, IAssetRequester requester, IAssetVariantDescriptor? descriptor = null) where A : LoadableAsset, new()
    {
        if (assetURL == null || requester == null)
            return;

        var key = new AssetID(assetURL, typeof(A));
        AssetVariantManager manager;
        lock (_managerLock)
        {
            if (!_variantManagers.TryGetValue(key, out manager!))
            {
                manager = new AssetVariantManager<A>(assetURL, this);
                _variantManagers[key] = manager;
            }
        }
        manager.RequestAsset(requester, descriptor);
    }

    public void ReleaseAsset<A>(Uri assetURL, IAssetRequester requester, IAssetVariantDescriptor? descriptor = null) where A : LoadableAsset, new()
    {
        if (assetURL == null || requester == null)
            return;

        AssetVariantManager? manager;
        lock (_managerLock)
        {
            _variantManagers.TryGetValue(new AssetID(assetURL, typeof(A)), out manager);
        }
        manager?.RemoveRequest(requester, descriptor);
    }

    // Drops the shared instance of one variant of an address once nothing asks for it, for a reload that has to
    // decode again rather than land on the instance already loaded. False while something still holds it.
    internal bool Forget<A>(Uri assetURL, IAssetVariantDescriptor? descriptor) where A : LoadableAsset, new()
    {
        AssetVariantManager? manager;
        lock (_managerLock)
        {
            _variantManagers.TryGetValue(new AssetID(assetURL, typeof(A)), out manager);
        }
        return manager == null || manager.Forget(descriptor);
    }

    // Whoever still asks for a failed instance of one address, for a part that may arrive after all.
    internal void CollectFailed(AssetID id, List<IAssetRequester> into)
    {
        AssetVariantManager? manager;
        lock (_managerLock)
        {
            _variantManagers.TryGetValue(id, out manager);
        }
        manager?.CollectFailed(into);
    }

    // Called by a variant manager once its asset unloads. Removal is deferred to Update so we
    // don't mutate the dictionary from an unload task thread mid-iteration.
    internal void ScheduleVariantManagerRemoval(AssetID id)
    {
        lock (_managerLock)
        {
            _managersToRemove.Add(id);
        }
    }

    // Gather completion is still pumped by the world loop; both pumps move here once the integration
    // queue lands.
    public void Update(float deltaTime)
    {
        TexturePixelCache.Tick();
        LateCloudParts.Tick(this);

        lock (_managerLock)
        {
            if (_managersToRemove.Count == 0)
                return;

            foreach (var id in _managersToRemove)
            {
                if (_variantManagers.TryGetValue(id, out var manager) && manager.RequestCount == 0)
                    _variantManagers.Remove(id);
            }
            _managersToRemove.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposing)
            return;

        _disposing = true;
        VariantWorkers.Shutdown();
        Logger.Log("AssetManager: Disposed");
    }
}
