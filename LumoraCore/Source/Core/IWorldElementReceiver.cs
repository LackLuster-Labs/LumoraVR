namespace Lumora.Core;

/// <summary>
/// Interface for receiving async notifications when world elements become available.
/// Used by SyncRef and other reference types for deferred resolution.
/// </summary>
public interface IWorldElementReceiver
{
    /// <summary>
    /// Called when a requested world element becomes available.
    /// </summary>
    /// <param name="element">The element that is now available.</param>
    void OnWorldElementAvailable(IWorldElement element);
}
