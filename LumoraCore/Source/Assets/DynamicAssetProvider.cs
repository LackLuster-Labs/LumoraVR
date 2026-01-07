using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for asset providers that create and manage dynamic (procedural) assets.
/// Unlike URL-based providers, these assets are generated at runtime.
/// </summary>
/// <typeparam name="A">The asset type to provide</typeparam>
public abstract class DynamicAssetProvider<A> : AssetProvider<A> where A : Asset, new()
{
    private A _asset;

    /// <summary>
    /// Whether this asset should be processed with high priority.
    /// </summary>
    public readonly Sync<bool> HighPriorityIntegration;

    /// <summary>
    /// When true, asset updates must be triggered manually via RunManualUpdate().
    /// </summary>
    public bool LocalManualUpdate { get; set; }

    public override A Asset => _asset;

    public override bool IsAssetAvailable => _asset != null;

    protected DynamicAssetProvider()
    {
        HighPriorityIntegration = new Sync<bool>(this, false);
    }

    /// <summary>
    /// Manually trigger an asset update. Only works if LocalManualUpdate is true.
    /// </summary>
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
        _asset.HighPriorityIntegration = HighPriorityIntegration.Value;
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
            _asset = null;
            OnAssetCleared();
            AssetRemoved();
        }
    }

    /// <summary>
    /// Called when a new asset instance is created.
    /// </summary>
    protected abstract void OnAssetCreated(A asset);

    /// <summary>
    /// Update the asset data. Called whenever the asset needs to be regenerated.
    /// </summary>
    protected abstract void UpdateAsset(A asset);

    /// <summary>
    /// Called when the asset is cleared/freed.
    /// </summary>
    protected abstract void OnAssetCleared();

    /// <summary>
    /// Mark the asset as needing an update.
    /// </summary>
    protected void MarkChangeDirty()
    {
        if (AssetReferenceCount > 0)
        {
            UpdateAsset();
        }
    }
}
