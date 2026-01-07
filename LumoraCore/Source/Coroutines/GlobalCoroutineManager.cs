using System;
using System.Collections;
using System.Collections.Generic;
using Lumora.Core.Logging;

namespace Lumora.Core.Coroutines;

/// <summary>
/// Global coroutine manager for engine-level async operations.
/// </summary>
public class GlobalCoroutineManager : IDisposable
{
    private readonly List<Coroutine> _activeCoroutines = new List<Coroutine>();
    private readonly Queue<Coroutine> _coroutinesToAdd = new Queue<Coroutine>();
    private readonly Queue<Coroutine> _coroutinesToRemove = new Queue<Coroutine>();
    private bool _isUpdating = false;

    /// <summary>
    /// Statistics
    /// </summary>
    public int ActiveCoroutineCount => _activeCoroutines.Count;
    public int TotalCoroutinesStarted { get; private set; }
    public int TotalCoroutinesCompleted { get; private set; }

    /// <summary>
    /// Start a new coroutine.
    /// </summary>
    public Coroutine StartCoroutine(IEnumerator routine, string name = null)
    {
        if (routine == null)
            throw new ArgumentNullException(nameof(routine));

        var coroutine = new Coroutine(routine, name ?? "GlobalCoroutine");

        if (_isUpdating)
        {
            _coroutinesToAdd.Enqueue(coroutine);
        }
        else
        {
            _activeCoroutines.Add(coroutine);
        }

        TotalCoroutinesStarted++;
        Logger.Log($"GlobalCoroutineManager: Started coroutine '{coroutine.Name}'");

        return coroutine;
    }

    /// <summary>
    /// Stop a running coroutine.
    /// </summary>
    public void StopCoroutine(Coroutine coroutine)
    {
        if (coroutine == null)
            return;

        coroutine.Stop();

        if (_isUpdating)
        {
            _coroutinesToRemove.Enqueue(coroutine);
        }
        else
        {
            _activeCoroutines.Remove(coroutine);
            TotalCoroutinesCompleted++;
        }
    }

    /// <summary>
    /// Stop all running coroutines.
    /// </summary>
    public void StopAllCoroutines()
    {
        foreach (var coroutine in _activeCoroutines)
        {
            coroutine.Stop();
        }

        _activeCoroutines.Clear();
        _coroutinesToAdd.Clear();
        _coroutinesToRemove.Clear();

        Logger.Log("GlobalCoroutineManager: Stopped all coroutines");
    }

    /// <summary>
    /// Update all active coroutines.
    /// </summary>
    public void Update(float deltaTime)
    {
        _isUpdating = true;

        // Process queued additions
        while (_coroutinesToAdd.Count > 0)
        {
            _activeCoroutines.Add(_coroutinesToAdd.Dequeue());
        }

        // Update active coroutines
        for (int i = _activeCoroutines.Count - 1; i >= 0; i--)
        {
            var coroutine = _activeCoroutines[i];

            if (!coroutine.Update(deltaTime))
            {
                // Coroutine finished
                _activeCoroutines.RemoveAt(i);
                TotalCoroutinesCompleted++;
                Logger.Log($"GlobalCoroutineManager: Coroutine '{coroutine.Name}' completed");
            }
        }

        // Process queued removals
        while (_coroutinesToRemove.Count > 0)
        {
            var coroutine = _coroutinesToRemove.Dequeue();
            _activeCoroutines.Remove(coroutine);
            TotalCoroutinesCompleted++;
        }

        _isUpdating = false;
    }

    /// <summary>
    /// Dispose of the coroutine manager.
    /// </summary>
    public void Dispose()
    {
        StopAllCoroutines();
        Logger.Log("GlobalCoroutineManager: Disposed");
    }
}

/// <summary>
/// Represents a running coroutine.
/// </summary>
public class Coroutine
{
    private IEnumerator _routine;
    private object _current;
    private bool _isRunning;
    private float _waitTimer;

    public string Name { get; }
    public bool IsRunning => _isRunning;
    public object Current => _current;

    public Coroutine(IEnumerator routine, string name)
    {
        _routine = routine ?? throw new ArgumentNullException(nameof(routine));
        Name = name;
        _isRunning = true;
        _waitTimer = 0f;
    }

    /// <summary>
    /// Update the coroutine.
    /// </summary>
    /// <returns>True if still running, false if completed.</returns>
    public bool Update(float deltaTime)
    {
        if (!_isRunning)
            return false;

        // Handle wait timer
        if (_waitTimer > 0)
        {
            _waitTimer -= deltaTime;
            return true;
        }

        // Process yield instructions
        if (_current is WaitForSeconds waitForSeconds)
        {
            _waitTimer = waitForSeconds.Seconds;
            _current = null;
            return true;
        }
        else if (_current is WaitForEndOfFrame)
        {
            // Continue next frame
            _current = null;
            return true;
        }
        else if (_current is WaitUntil waitUntil)
        {
            if (!waitUntil.Condition())
                return true;
            _current = null;
        }
        else if (_current is WaitWhile waitWhile)
        {
            if (waitWhile.Condition())
                return true;
            _current = null;
        }

        // Advance the coroutine
        try
        {
            if (_routine.MoveNext())
            {
                _current = _routine.Current;
                return true;
            }
            else
            {
                // Coroutine completed
                _isRunning = false;
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Coroutine '{Name}' exception: {ex.Message}");
            _isRunning = false;
            return false;
        }
    }

    /// <summary>
    /// Stop the coroutine.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _routine = null;
        _current = null;
    }
}

/// <summary>
/// Yield instruction to wait for a specified number of seconds.
/// </summary>
public class WaitForSeconds
{
    public float Seconds { get; }

    public WaitForSeconds(float seconds)
    {
        Seconds = seconds > 0 ? seconds : 0;
    }
}

/// <summary>
/// Yield instruction to wait until the end of the current frame.
/// </summary>
public class WaitForEndOfFrame
{
}

/// <summary>
/// Yield instruction to wait until a condition is true.
/// </summary>
public class WaitUntil
{
    public Func<bool> Condition { get; }

    public WaitUntil(Func<bool> condition)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
    }
}

/// <summary>
/// Yield instruction to wait while a condition is true.
/// </summary>
public class WaitWhile
{
    public Func<bool> Condition { get; }

    public WaitWhile(Func<bool> condition)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
    }
}

/// <summary>
/// Extension methods for starting coroutines.
/// </summary>
public static class CoroutineExtensions
{
    /// <summary>
    /// Start a coroutine on the global manager.
    /// </summary>
    public static Coroutine StartGlobalCoroutine(this IEnumerator routine, string name = null)
    {
        if (Engine.Current?.CoroutineManager != null)
        {
            return Engine.Current.CoroutineManager.StartCoroutine(routine, name);
        }

        Logger.Warn("Cannot start global coroutine - Engine or CoroutineManager not initialized");
        return null;
    }
}