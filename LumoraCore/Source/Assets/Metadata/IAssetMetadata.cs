namespace Lumora.Core.Assets;

/// <summary>
/// Base interface for asset metadata.
/// Provides information about an asset before or during loading.
/// </summary>
public interface IAssetMetadata
{
    /// <summary>
    /// Estimated memory size in bytes for this asset when loaded.
    /// </summary>
    long EstimatedMemorySize { get; }
}
