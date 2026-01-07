using System;
using System.Threading;

namespace Lumora.Core;

/// <summary>
/// Manages thread-safe locking for World modifications.
/// Uses a proper mutex to allow threads to wait for the lock.
/// </summary>
public class HookManager : IDisposable
{
    public enum LockOwner
    {
        None,
        DataModel,  // Main sync thread
        Implementer // Godot main thread
    }

    private readonly object _lockObj = new object();
    private Thread _lockingThread;

    public World Owner { get; private set; }
    public LockOwner Lock { get; private set; }

    /// <summary>
    /// Whether the current thread can modify world state.
    /// Modifications are allowed when:
    /// - World is not running (initialization phase)
    /// - No lock is held (between update cycles)
    /// - Current thread holds the lock
    /// </summary>
    public bool CanCurrentThreadModify
    {
        get
        {
            // Always allow modifications when world is not running
            if (Owner.State != World.WorldState.Running)
                return true;

            // Allow modifications when no lock is held (between cycles)
            if (Lock == LockOwner.None)
                return true;

            // Allow if current thread holds the lock
            return Thread.CurrentThread == _lockingThread;
        }
    }

    public HookManager(World owner)
    {
        Owner = owner;
    }

    /// <summary>
    /// Verify the current thread can modify world state.
    /// Throws if modification is not allowed.
    /// </summary>
    public void ThreadCheck()
    {
        if (!CanCurrentThreadModify)
        {
            throw new Exception($"Modifications from non-locking thread disallowed! Current lock: {Lock}");
        }
    }

    /// <summary>
    /// Lock for DataModel (sync thread) modifications.
    /// Waits if another thread has the lock.
    /// </summary>
    public void DataModelLock(Thread ownerThread)
    {
        Monitor.Enter(_lockObj);
        _lockingThread = ownerThread;
        Lock = LockOwner.DataModel;
    }

    /// <summary>
    /// Unlock DataModel modifications.
    /// </summary>
    public void DataModelUnlock()
    {
        if (Lock != LockOwner.DataModel)
        {
            return; // Silently ignore if not locked by DataModel
        }
        _lockingThread = null;
        Lock = LockOwner.None;
        Monitor.Exit(_lockObj);
    }

    /// <summary>
    /// Lock for Implementer (main thread) modifications.
    /// Waits if another thread has the lock.
    /// </summary>
    public void ImplementerLock(Thread ownerThread)
    {
        Monitor.Enter(_lockObj);
        _lockingThread = ownerThread;
        Lock = LockOwner.Implementer;
    }

    /// <summary>
    /// Unlock Implementer modifications.
    /// </summary>
    public void ImplementerUnlock()
    {
        if (Lock != LockOwner.Implementer)
        {
            return; // Silently ignore if not locked by Implementer
        }
        _lockingThread = null;
        Lock = LockOwner.None;
        Monitor.Exit(_lockObj);
    }

    public void Dispose()
    {
        Owner = null;
        Lock = LockOwner.None;
        _lockingThread = null;
    }
}
