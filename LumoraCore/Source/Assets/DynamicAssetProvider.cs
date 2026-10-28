// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;

namespace Lumora.Core.Assets;

public abstract class DynamicAssetProvider<A> : AssetProvider<A>, IProceduralAssetProvider where A : Asset, new()
{
    private A _asset = null!;

    public readonly Sync<bool> HighPriorityIntegration;

    public bool LocalManualUpdate { get; set; }

    public override A Asset => _asset;

    public override bool IsAssetAvailable => _asset != null;

    protected DynamicAssetProvider()
    {
        HighPriorityIntegration = new Sync<bool>(this, false);
    }

    // No-op unless LocalManualUpdate is set.
    public void RunManualUpdate()
    {
        if (!LocalManualUpdate)
        {
            throw new InvalidOperationException("This asset provider is not configured for manual updates.");
        }
        RunAssetUpdate();
    }

    protected override void UpdateAsset()
    {
        if (!LocalManualUpdate)
        {
            RunAssetUpdate();
        }
    }

    private void RunAssetUpdate()
    {
        Lumora.Core.Logging.Logger.Debug($"DynamicAssetProvider.RunAssetUpdate: [{GetType().Name}] Starting");
        bool isNew = _asset == null;
        if (isNew)
        {
            _asset = new A();
            _asset.InitializeDynamic();
            _asset.SetOwner(this);
            OnAssetCreated(_asset);
            Lumora.Core.Logging.Logger.Debug($"DynamicAssetProvider.RunAssetUpdate: [{GetType().Name}] Created new asset");
        }
        _asset!.HighPriorityIntegration = HighPriorityIntegration.Value;
        Lumora.Core.Logging.Logger.Debug($"DynamicAssetProvider.RunAssetUpdate: [{GetType().Name}] Calling UpdateAsset(_asset)");
        UpdateAsset(_asset);

        // Notify references that asset was updated (so MeshRenderer can re-apply material)
        AssetUpdated();
    }

    protected override void FreeAsset()
    {
        if (_asset != null)
        {
            _asset.Unload();
            _asset = null!;
            OnAssetCleared();
            AssetRemoved();
        }
    }

    protected abstract void OnAssetCreated(A asset);

    protected abstract void UpdateAsset(A asset);

    protected abstract void OnAssetCleared();

    // Safe from any thread, defers the Godot work to the main thread.
    protected new void MarkChangeDirty()
    {
        if (AssetReferenceCount > 0)
        {
            // Sync field changes fire on the SyncLoop thread.
            // Defer UpdateAsset() so Godot resources are only touched on the main thread.
            var world = World;
            if (world == null)
            {
                UpdateAsset();
                return;
            }
            // One update waits at a time and reads the provider as it is when it runs: changes landing together
            // queued an update each. -xlinka
            if (Interlocked.Exchange(ref _updateQueued, 1) == 0)
                world.RunSynchronously(_queuedUpdate ??= RunQueuedUpdate);
        }
    }

    private int _updateQueued;
    private Action? _queuedUpdate;

    private void RunQueuedUpdate()
    {
        Volatile.Write(ref _updateQueued, 0);
        UpdateAsset();
    }
}
