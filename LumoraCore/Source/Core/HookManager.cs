using System;
using System.Threading;

namespace Lumora.Core;

/// <summary>
/// Manages thread-safe locking for World modifications.
/// </summary>
public class HookManager : IDisposable
{
    public enum LockOwner
    {
        None,
        DataModel,  // Main sync thread
        Implementer // Godot main thread
    }

    private Thread _lockingThread;

    public World Owner { get; private set; }
    public LockOwner Lock { get; private set; }

    /// <summary>
    /// Whether the current thread can modify world state.
    /// </summary>
    public bool CanCurrentThreadModify
    {
        get
        {
            if (Thread.CurrentThread != _lockingThread)
            {
                return Owner.State != World.WorldState.Running;
            }
            return true;
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
    /// </summary>
    public void DataModelLock(Thread ownerThread)
    {
        if (Lock != LockOwner.None)
        {
            throw new Exception("DataModel cannot lock - already locked!");
        }
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
            throw new Exception("DataModel cannot unlock - not locked by DataModel!");
        }
        _lockingThread = null;
        Lock = LockOwner.None;
    }

    /// <summary>
    /// Lock for Implementer (main thread) modifications.
    /// </summary>
    public void ImplementerLock(Thread ownerThread)
    {
        if (Lock != LockOwner.None)
        {
            throw new Exception("Implementer cannot lock - already locked!");
        }
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
            throw new Exception("Implementer cannot unlock - not locked by Implementer!");
        }
        _lockingThread = null;
        Lock = LockOwner.None;
    }

    public void Dispose()
    {
        Owner = null;
        Lock = LockOwner.None;
        _lockingThread = null;
    }
}
