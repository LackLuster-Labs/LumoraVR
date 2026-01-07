namespace Lumora.Core.Assets;

/// <summary>
/// Interface for objects that consume assets and need to be notified of asset state changes.
/// Provides callback-based notifications for asset lifecycle events.
/// </summary>
public interface IAssetConsumer
{
    /// <summary>
    /// Called when an asset is assigned to this consumer.
    /// </summary>
    void OnAssetAssigned(Asset asset);

    /// <summary>
    /// Called when the asset's load state changes.
    /// </summary>
    void OnAssetStateChanged(Asset asset);
}
