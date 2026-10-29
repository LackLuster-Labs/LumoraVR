// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Threading;

namespace Lumora.Core;

public class HookManager : IDisposable
{
    public enum LockOwner
    {
        None,
        DataModel,  // Main sync thread
        Implementer // Godot main thread
    }

    private readonly object _lockObj = new object();

    // Read by every thread that writes to the world, written only by the thread taking or letting go of the
    // lock. Taking sets the owner before the flag and letting go clears the flag before the owner, so a
    // reader that sees the flag set always sees who holds it, and one that races a release sees it free:
    // clearing them the other way round made a thread refuse a write in the instant after the holder was
    // already done, naming no holder at all. -xlinka
    private volatile Thread? _lockingThread;
    private volatile LockOwner _lock;
    // Takes held by the holding thread; touched only while holding.
    private int _depth;

    public World Owner { get; private set; }
    public LockOwner Lock => _lock;

    // Whether the current thread can modify world state.
    // Modifications are allowed when:
    // - World is not running (initialization phase)
    // - No lock is held (between update cycles)
    // - Current thread holds the lock
    public bool CanCurrentThreadModify
    {
        get
        {
            if (Owner.State != World.WorldState.Running)
                return true;

            if (_lock == LockOwner.None)
                return true;

            // The holder may have let go between the two reads; free now is free.
            return Thread.CurrentThread == _lockingThread || _lock == LockOwner.None;
        }
    }

    // Whether the calling thread holds the world right now: an update, a sync cycle, or a hold of its own.
    public bool HeldByCurrentThread => _lock != LockOwner.None && _lockingThread == Thread.CurrentThread;

    public HookManager(World owner)
    {
        Owner = owner;
    }

    // Throws when modification is not allowed.
    public void ThreadCheck()
    {
        if (Owner.State != World.WorldState.Running)
            return;
        // Decided from the same reads the message shows, in the order the fields are written.
        var held = _lock;
        if (held == LockOwner.None)
            return;
        var holder = _lockingThread;
        if (holder == Thread.CurrentThread || _lock == LockOwner.None)
            return;

        // Name the world and both threads. This check runs on the world being WRITTEN TO, which is
        // not always the world whose update is running: something in world A reaching into world B
        // while B's own thread holds B lands here, and reads identically to a plain threading fault
        // unless the message says which world refused and who was holding it. -xlinka
        throw new Exception($"Modifications from non-locking thread disallowed! Current lock: {held} on world " +
            $"'{Owner?.Name ?? "?"}', held by thread {holder?.ManagedThreadId.ToString() ?? "none"}, " +
            $"current thread {Environment.CurrentManagedThreadId}");
    }

    // Waits if another thread holds the lock.
    public void DataModelLock(Thread ownerThread) => Take(ownerThread, LockOwner.DataModel);

    public void DataModelUnlock() => Release(LockOwner.DataModel);

    // Waits if another thread holds the lock.
    public void ImplementerLock(Thread ownerThread) => Take(ownerThread, LockOwner.Implementer);

    public void ImplementerUnlock() => Release(LockOwner.Implementer);

    // The holder can take the world again (an update pumped from inside an update), so every take gets one
    // exit and only the outermost release hands the world back. Releasing on the owner flag alone let the
    // inner release clear the flag, the outer one was then ignored, and the world thread kept the monitor for
    // good: the sync thread waited on it forever and that peer never sent or applied another change. -xlinka
    private void Take(Thread ownerThread, LockOwner kind)
    {
        Monitor.Enter(_lockObj);
        if (_depth++ > 0)
            return;
        _lockingThread = ownerThread;
        _lock = kind;
    }

    // A release from a thread not holding it, or of the wrong kind at the outermost take, is ignored, so the
    // sync loop's second release after a clean one stays harmless.
    private void Release(LockOwner kind)
    {
        if (!Monitor.IsEntered(_lockObj) || (_depth == 1 && _lock != kind))
            return;
        if (--_depth == 0)
        {
            _lock = LockOwner.None;
            _lockingThread = null;
        }
        Monitor.Exit(_lockObj);
    }

    public void Dispose()
    {
        Owner = null!;
        _lock = LockOwner.None;
        _lockingThread = null;
    }
}
