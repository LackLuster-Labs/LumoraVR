using System;
using System.Collections.Generic;

namespace Lumora.Core;

/// <summary>
/// Manages component updates with bucketed ordering.
/// Provides deterministic update execution with ordered buckets.
/// </summary>
public class UpdateManager
{
    private World _world;
    private HashSet<IImplementable> _pendingHookUpdates = new HashSet<IImplementable>();
    private readonly object _hookUpdatesLock = new object();
    private float _currentDeltaTime = 0f;

    // Bucketed update system for ordered execution
    private SortedDictionary<int, List<IUpdatable>> _updateBuckets = new SortedDictionary<int, List<IUpdatable>>();
    private Queue<IUpdatable> _startupQueue = new Queue<IUpdatable>();
    private Queue<IUpdatable> _destructionQueue = new Queue<IUpdatable>();
    private SortedDictionary<int, Queue<IUpdatable>> _changeBuckets = new SortedDictionary<int, Queue<IUpdatable>>();
    private int _changeUpdateIndex = 0;
    private Dictionary<IInitializable, List<IInitializable>> _initializableChildren = new Dictionary<IInitializable, List<IInitializable>>();

    // Currently updating component (for debugging)
    public IUpdatable CurrentlyUpdating { get; private set; }

    public UpdateManager(World world)
    {
        _world = world;
    }

    /// <summary>
    /// Gets the current delta time for this frame.
    /// Used by hooks during ApplyChanges.
    /// </summary>
    public float DeltaTime => _currentDeltaTime;

    // ===== Registration Methods =====

    /// <summary>
    /// Register a component for startup (runs before first update).
    /// </summary>
    public void RegisterForStartup(IUpdatable updatable)
    {
        if (updatable != null && !updatable.IsDestroyed)
        {
            _startupQueue.Enqueue(updatable);
        }
    }

    /// <summary>
    /// Register a component for updates (runs every frame).
    /// </summary>
    public void RegisterForUpdates(IUpdatable updatable)
    {
        if (updatable == null || updatable.IsDestroyed)
            return;

        int order = updatable.UpdateOrder;
        if (!_updateBuckets.TryGetValue(order, out var bucket))
        {
            bucket = new List<IUpdatable>();
            _updateBuckets[order] = bucket;
        }
        if (!bucket.Contains(updatable))
        {
            bucket.Add(updatable);
        }
    }

    /// <summary>
    /// Unregister a component from updates.
    /// </summary>
    public void UnregisterFromUpdates(IUpdatable updatable)
    {
        if (updatable == null)
            return;

        int order = updatable.UpdateOrder;
        if (_updateBuckets.TryGetValue(order, out var bucket))
        {
            bucket.Remove(updatable);
        }
    }

    /// <summary>
    /// Called when a component's UpdateOrder changes.
    /// </summary>
    public void UpdateBucketChanged(IUpdatable updatable)
    {
        if (updatable == null)
            return;

        // Remove from all buckets and re-add to correct one
        foreach (var bucket in _updateBuckets.Values)
        {
            bucket.Remove(updatable);
        }

        if (!updatable.IsDestroyed && updatable.IsStarted)
        {
            RegisterForUpdates(updatable);
        }
    }

    /// <summary>
    /// Register a component for change application.
    /// </summary>
    public void RegisterForChanges(IUpdatable updatable)
    {
        if (updatable == null || updatable.IsDestroyed)
            return;

        int order = updatable.UpdateOrder;
        if (!_changeBuckets.TryGetValue(order, out var queue))
        {
            queue = new Queue<IUpdatable>();
            _changeBuckets[order] = queue;
        }
        queue.Enqueue(updatable);
    }

    /// <summary>
    /// Register a component for destruction.
    /// </summary>
    public void RegisterForDestruction(IUpdatable updatable)
    {
        if (updatable != null)
        {
            _destructionQueue.Enqueue(updatable);
        }
    }

    /// <summary>
    /// Register a component for hook update.
    /// Called when component properties change.
    /// </summary>
    public void RegisterHookUpdate(IImplementable component)
    {
        if (component != null && component.Hook != null)
        {
            lock (_hookUpdatesLock)
            {
                _pendingHookUpdates.Add(component);
            }
        }
    }

    // ===== Update Execution =====

    /// <summary>
    /// Run all startup callbacks.
    /// </summary>
    public void RunStartups()
    {
        while (_startupQueue.Count > 0)
        {
            var updatable = _startupQueue.Dequeue();
            if (!updatable.IsDestroyed)
            {
                try
                {
                    CurrentlyUpdating = updatable;
                    updatable.InternalRunStartup();
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error in startup for {updatable}: {ex.Message}");
                }
                finally
                {
                    CurrentlyUpdating = null;
                }
            }
        }
    }

    /// <summary>
    /// Run all component updates in bucket order.
    /// </summary>
    public void RunUpdates(float deltaTime)
    {
        _currentDeltaTime = deltaTime;

        foreach (var kvp in _updateBuckets)
        {
            var bucket = kvp.Value;
            for (int i = 0; i < bucket.Count; i++)
            {
                var updatable = bucket[i];
                if (!updatable.IsDestroyed)
                {
                    try
                    {
                        CurrentlyUpdating = updatable;
                        updatable.InternalRunUpdate();
                    }
                    catch (Exception ex)
                    {
                        Logging.Logger.Error($"UpdateManager: Error in update for {updatable}: {ex.Message}");
                    }
                    finally
                    {
                        CurrentlyUpdating = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Run all change application callbacks in bucket order.
    /// </summary>
    public void RunChangeApplications()
    {
        _changeUpdateIndex++;

        foreach (var kvp in _changeBuckets)
        {
            var queue = kvp.Value;
            while (queue.Count > 0)
            {
                var updatable = queue.Dequeue();
                if (!updatable.IsDestroyed && updatable.IsChangeDirty)
                {
                    try
                    {
                        CurrentlyUpdating = updatable;
                        updatable.InternalRunApplyChanges(_changeUpdateIndex);
                    }
                    catch (Exception ex)
                    {
                        Logging.Logger.Error($"UpdateManager: Error in change application for {updatable}: {ex.Message}");
                    }
                    finally
                    {
                        CurrentlyUpdating = null;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Run all destruction callbacks.
    /// </summary>
    public void RunDestructions()
    {
        while (_destructionQueue.Count > 0)
        {
            var updatable = _destructionQueue.Dequeue();
            try
            {
                CurrentlyUpdating = updatable;
                updatable.InternalRunDestruction();
            }
            catch (Exception ex)
            {
                Logging.Logger.Error($"UpdateManager: Error in destruction for {updatable}: {ex.Message}");
            }
            finally
            {
                CurrentlyUpdating = null;
            }
        }
    }

    /// <summary>
    /// Process all pending hook updates.
    /// Called by the world renderer after component updates.
    /// </summary>
    public void ProcessHookUpdates(float deltaTime)
    {
        List<IImplementable> pending;
        lock (_hookUpdatesLock)
        {
            if (_pendingHookUpdates.Count == 0)
                return;

            // Snapshot to avoid collection modification during hook updates
            pending = new List<IImplementable>(_pendingHookUpdates);
            _pendingHookUpdates.Clear();
        }

        // Store delta time for hooks to access
        _currentDeltaTime = deltaTime;

        foreach (var component in pending)
        {
            if (component is ImplementableComponent<IHook> impl)
            {
                impl.UpdateHook();
            }
        }
    }

    /// <summary>
    /// Clear all pending updates.
    /// </summary>
    public void Clear()
    {
        lock (_hookUpdatesLock)
        {
            _pendingHookUpdates.Clear();
        }
        _updateBuckets.Clear();
        _startupQueue.Clear();
        _destructionQueue.Clear();
        _changeBuckets.Clear();
        _initializableChildren.Clear();
    }

    /// <summary>
    /// Track a child initializable so its init phase can be ended when the parent finishes.
    /// </summary>
    public void AddInitializableChild(IInitializable parent, IInitializable child)
    {
        if (parent == null || child == null)
            return;

        if (!_initializableChildren.TryGetValue(parent, out var list))
        {
            list = new List<IInitializable>();
            _initializableChildren[parent] = list;
        }

        list.Add(child);
    }

    /// <summary>
    /// End initialization phase on all tracked children of the given parent.
    /// </summary>
    public void EndInitPhaseInChildren(IInitializable parent)
    {
        if (parent == null)
            return;

        if (_initializableChildren.TryGetValue(parent, out var children))
        {
            foreach (var child in children)
            {
                try
                {
                    if (child.IsInInitPhase)
                    {
                        child.EndInitPhase();
                    }
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error($"UpdateManager: Error ending init phase for {child}: {ex.Message}");
                }
            }

            _initializableChildren.Remove(parent);
        }
    }
}
