using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AquaLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Core.Assets;

/// <summary>
/// Base class for all assets in Lumora.
/// Implements thread-safe locking mechanism for concurrent asset access.
/// Supports both static (loaded from external sources) and dynamic (procedural) assets.
/// </summary>
public abstract class Asset : IAsset
{
    private readonly struct LockRequest
    {
        public readonly bool write;
        public readonly object lockObject;
        public readonly Action<IAsset> callback;

        public LockRequest(bool write, object lockObject, Action<IAsset> callback)
        {
            if (lockObject == null)
            {
                throw new ArgumentNullException(nameof(lockObject));
            }
            this.write = write;
            this.lockObject = lockObject;
            this.callback = callback;
        }
    }

    // Locking state
    private SpinLock lockRequestLock = new SpinLock(enableThreadOwnerTracking: false);
    private List<object> readLocks = new List<object>();
    private object writeLock;
    private Queue<LockRequest> lockRequests = new Queue<LockRequest>();

    // Asset properties
    public bool HighPriorityIntegration { get; set; }
    internal int UnloadKey { get; set; }
    public int Version { get; protected set; }
    public object Owner { get; protected set; }
    public AssetType AssetType { get; protected set; }
    public AssetLoadState LoadState { get; protected set; }
    public Uri AssetURL { get; private set; }

    /// <summary>
    /// Number of active requests for this asset.
    /// </summary>
    public abstract int ActiveRequestCount { get; }

    /// <summary>
    /// Delay (in seconds) before unloading unused assets.
    /// </summary>
    public virtual float UnloadDelay => 5.0f; // Default 5 seconds

    // ===== INITIALIZATION =====

    /// <summary>
    /// Initialize a static asset with a URL.
    /// </summary>
    public virtual void InitializeStatic(Uri assetUrl)
    {
        AssetURL = assetUrl;
        AssetType = AssetType.Static;
        LoadState = AssetLoadState.Created;
    }

    /// <summary>
    /// Initialize a dynamic (procedural) asset.
    /// </summary>
    public virtual void InitializeDynamic()
    {
        AssetType = AssetType.Dynamic;
        LoadState = AssetLoadState.FullyLoaded; // Dynamic assets are immediately "loaded"
    }

    /// <summary>
    /// Set the owner of this asset instance.
    /// </summary>
    public void SetOwner(object owner)
    {
        if (Owner != null)
        {
            throw new Exception($"Owner already set: {Owner}, newOwner: {owner}");
        }
        Owner = owner;
    }

    /// <summary>
    /// Set the asset URL (only for static assets during initialization).
    /// </summary>
    public virtual void SetURL(Uri assetUrl)
    {
        CheckStatic();
        if (AssetURL != null)
        {
            throw new Exception("Asset URL is already set!");
        }
        AssetURL = assetUrl;
    }

    // ===== LOAD STATE MANAGEMENT =====

    protected void CheckStatic()
    {
        if (AssetType != AssetType.Static)
        {
            throw new Exception("Asset instance isn't Static");
        }
    }

    protected void CheckDynamic()
    {
        if (AssetType != AssetType.Dynamic)
        {
            throw new Exception("Asset instance isn't dynamic");
        }
    }

    protected void FailLoad(string reason)
    {
        AquaLogger.Log($"Failed Load: {reason}, for: {AssetURL}, instance: {GetHashCode()}");
        SetLoadState(AssetLoadState.Failed);
    }

    protected void SetLoadState(AssetLoadState state)
    {
        CheckStatic();
        if (LoadState != state)
        {
            if (LoadState > state)
            {
                throw new Exception("Cannot revert load state to an earlier stage.");
            }
            LoadState = state;
            OnLoadStateChanged();
        }
    }

    protected virtual void OnLoadStateChanged()
    {
        // Override in derived classes to handle load state changes
    }

    // ===== ABSTRACT METHODS =====

    /// <summary>
    /// Queue this asset for unloading.
    /// </summary>
    public abstract void Unload();

    // ===== LOCKING SYSTEM =====

    public void RequestWrite(Action<IAsset> callback)
    {
        RequestCallback(write: true, callback);
    }

    public void RequestRead(Action<IAsset> callback)
    {
        RequestCallback(write: false, callback);
    }

    public Task<IAsset> RequestWrite()
    {
        return RequestTask(write: true);
    }

    public Task<IAsset> RequestRead()
    {
        return RequestTask(write: false);
    }

    private Task<IAsset> RequestTask(bool write)
    {
        TaskCompletionSource<IAsset> task = new TaskCompletionSource<IAsset>();
        RequestCallback(write, a => task.TrySetResult(a));
        return task.Task;
    }

    private void RequestCallback(bool write, Action<IAsset> callback)
    {
        object _lock = new object();
        RequestLock(write, _lock, a =>
        {
            try
            {
                callback(a);
            }
            finally
            {
                if (write)
                {
                    ReleaseWriteLock(_lock);
                }
                else
                {
                    ReleaseReadLock(_lock);
                }
            }
        });
    }

    public Task<IAsset> RequestWriteLock(object lockObject)
    {
        return RequestLock(write: true, lockObject);
    }

    public Task<IAsset> RequestReadLock(object lockObject)
    {
        return RequestLock(write: false, lockObject);
    }

    private Task<IAsset> RequestLock(bool write, object lockObject)
    {
        TaskCompletionSource<IAsset> task = new TaskCompletionSource<IAsset>();
        RequestLock(write, lockObject, a => task.TrySetResult(a));
        return task.Task;
    }

    public void RequestWriteLock(object lockObject, Action<IAsset> callback)
    {
        RequestLock(write: true, lockObject, callback);
    }

    public void RequestReadLock(object lockObject, Action<IAsset> callback)
    {
        RequestLock(write: false, lockObject, callback);
    }

    public void ReleaseWriteLock(object lockObject)
    {
        bool lockTaken = false;
        try
        {
            lockRequestLock.Enter(ref lockTaken);
            if (lockObject != writeLock)
            {
                throw new InvalidOperationException($"Current writeLock and passed lockObject do not match! writeLock: {writeLock?.GetHashCode()}, lockObject: {lockObject?.GetHashCode()}");
            }
            writeLock = null;
        }
        finally
        {
            if (lockTaken)
            {
                lockRequestLock.Exit();
            }
        }
        ProcessLockRequestQueue();
    }

    public void ReleaseReadLock(object lockObject)
    {
        bool lockTaken = false;
        bool shouldProcess = false;
        try
        {
            lockRequestLock.Enter(ref lockTaken);
            if (!readLocks.Remove(lockObject))
            {
                throw new InvalidOperationException($"Given readLock isn't currently locking. lockObject: {lockObject?.GetHashCode()}, readLocks.Count: {readLocks.Count}");
            }
            if (readLocks.Count == 0)
            {
                shouldProcess = true;
            }
        }
        finally
        {
            if (lockTaken)
            {
                lockRequestLock.Exit();
            }
        }
        if (shouldProcess)
        {
            ProcessLockRequestQueue();
        }
    }

    private void RequestLock(bool write, object lockObject, Action<IAsset> callback)
    {
        bool lockTaken = false;
        try
        {
            LockRequest item = new LockRequest(write, lockObject, callback);
            lockRequestLock.Enter(ref lockTaken);
            lockRequests.Enqueue(item);
        }
        finally
        {
            if (lockTaken)
            {
                lockRequestLock.Exit();
            }
        }
        ProcessLockRequestQueue();
    }

    private void ProcessLockRequestQueue()
    {
        bool lockTaken = false;
        List<Action<IAsset>> list = null;
        try
        {
            lockRequestLock.Enter(ref lockTaken);
            if (writeLock != null)
            {
                return;
            }
            while (lockRequests.Count > 0)
            {
                LockRequest lockRequest = lockRequests.Peek();
                if (lockRequest.write)
                {
                    if (readLocks.Count == 0)
                    {
                        lockRequests.Dequeue();
                        writeLock = lockRequest.lockObject;
                        list = new List<Action<IAsset>>();
                        list.Add(lockRequest.callback);
                    }
                    break;
                }
                lockRequests.Dequeue();
                readLocks.Add(lockRequest.lockObject);
                if (list == null)
                {
                    list = new List<Action<IAsset>>();
                }
                list.Add(lockRequest.callback);
            }
        }
        finally
        {
            if (lockTaken)
            {
                lockRequestLock.Exit();
            }
        }
        if (list == null)
        {
            return;
        }
        foreach (Action<IAsset> item in list)
        {
            item(this);
        }
    }
}
