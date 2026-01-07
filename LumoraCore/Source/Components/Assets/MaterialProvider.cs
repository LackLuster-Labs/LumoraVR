using System;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for material provider components.
/// Creates and manages MaterialAsset instances that sync across the network.
/// </summary>
[ComponentCategory("Assets/Materials")]
public abstract class MaterialProvider : DynamicAssetProvider<MaterialAsset>
{
    private Action _assetUpdatedCallback;

    /// <summary>
    /// The material type this provider creates.
    /// </summary>
    protected abstract MaterialType MaterialType { get; }

    /// <summary>
    /// Called when the asset is first created.
    /// Sets up the material type.
    /// </summary>
    protected override void OnAssetCreated(MaterialAsset asset)
    {
        asset.SetMaterialType(MaterialType);
    }

    /// <summary>
    /// Called to update the asset.
    /// Clears and rebuilds material properties.
    /// </summary>
    protected override void UpdateAsset(MaterialAsset asset)
    {
        Lumora.Core.Logging.Logger.Debug($"MaterialProvider.UpdateAsset: [{GetType().Name}] Updating material");
        asset.Clear();
        UpdateMaterial(asset);

        if (_assetUpdatedCallback == null)
        {
            _assetUpdatedCallback = () => AssetUpdated();
        }
        Lumora.Core.Logging.Logger.Debug($"MaterialProvider.UpdateAsset: [{GetType().Name}] Calling ApplyChanges");
        asset.ApplyChanges(_assetUpdatedCallback);
    }

    /// <summary>
    /// Override to set material properties.
    /// Called during asset update.
    /// </summary>
    protected abstract void UpdateMaterial(MaterialAsset asset);

    /// <summary>
    /// Called when asset is cleared.
    /// </summary>
    protected override void OnAssetCleared()
    {
        // Base implementation - override if needed
    }

    /// <summary>
    /// Force an immediate asset update.
    /// Call this after changing material properties programmatically.
    /// </summary>
    public void ForceUpdate()
    {
        MarkChangeDirty();
    }
}
