using System;
using System.Threading.Tasks;

namespace Lumora.Core.Assets;

/// <summary>
/// Base interface for all assets in Lumora.
/// Supports thread-safe read/write locking for safe concurrent access.
/// </summary>
public interface IAsset
{
    /// <summary>
    /// Request a write lock on the asset with a callback.
    /// The callback is invoked when the lock is acquired.
    /// </summary>
    void RequestWriteLock(object lockObject, Action<IAsset> callback);

    /// <summary>
    /// Request a read lock on the asset with a callback.
    /// Multiple readers can hold locks simultaneously.
    /// </summary>
    void RequestReadLock(object lockObject, Action<IAsset> callback);

    /// <summary>
    /// Release a previously acquired write lock.
    /// </summary>
    void ReleaseWriteLock(object lockObject);

    /// <summary>
    /// Release a previously acquired read lock.
    /// </summary>
    void ReleaseReadLock(object lockObject);

    /// <summary>
    /// Request a write lock asynchronously.
    /// Returns a Task that completes when the lock is acquired.
    /// </summary>
    Task<IAsset> RequestWriteLock(object lockObject);

    /// <summary>
    /// Request a read lock asynchronously.
    /// Returns a Task that completes when the lock is acquired.
    /// </summary>
    Task<IAsset> RequestReadLock(object lockObject);

    /// <summary>
    /// Request write access with automatic lock management.
    /// The callback is invoked with the lock held, then automatically released.
    /// </summary>
    void RequestWrite(Action<IAsset> callback);

    /// <summary>
    /// Request read access with automatic lock management.
    /// The callback is invoked with the lock held, then automatically released.
    /// </summary>
    void RequestRead(Action<IAsset> callback);

    /// <summary>
    /// Request write access asynchronously with automatic lock management.
    /// The lock is automatically released after the task completes.
    /// </summary>
    Task<IAsset> RequestWrite();

    /// <summary>
    /// Request read access asynchronously with automatic lock management.
    /// The lock is automatically released after the task completes.
    /// </summary>
    Task<IAsset> RequestRead();
}
