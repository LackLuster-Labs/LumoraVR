namespace Lumora.Core.Assets;

/// <summary>
/// Defines the type of asset for lifecycle management.
/// </summary>
public enum AssetType
{
    /// <summary>
    /// Static asset loaded from external source (file, URL, database).
    /// Has a persistent URL and can be unloaded/reloaded.
    /// </summary>
    Static = 0,

    /// <summary>
    /// Dynamic asset created procedurally at runtime.
    /// No external URL, exists only in memory during session.
    /// </summary>
    Dynamic = 1
}

/// <summary>
/// Represents the current load state of an asset.
/// </summary>
public enum AssetLoadState
{
    /// <summary>
    /// Asset instance created but loading hasn't started yet.
    /// </summary>
    Created = 0,

    /// <summary>
    /// Asset loading has been initiated.
    /// </summary>
    LoadStarted = 1,

    /// <summary>
    /// Asset is partially loaded (e.g., lower resolution variant).
    /// </summary>
    PartiallyLoaded = 2,

    /// <summary>
    /// Asset is fully loaded and ready to use.
    /// </summary>
    FullyLoaded = 3,

    /// <summary>
    /// Asset loading failed due to error.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Asset has been unloaded from memory.
    /// </summary>
    Unloaded = 5
}
