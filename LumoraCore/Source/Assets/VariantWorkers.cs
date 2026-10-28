// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Assets;

// Threads of their own for asset variants: texture ladders and mesh LOD chains. An import drops a hundred of
// them at once, and on the thread pool they ran as wide as the pool would go at the same priority as the frame,
// so the frame queued behind them. Here a few threads take them one at a time below normal priority, which
// leaves the game its cores, and a job that is cancelled before its turn is answered at once and never starts.
// Urgent jobs (a mesh that cannot show until its chain exists) go ahead of background ones (a ladder that only
// helps the next load).
//
// Jobs run synchronously on the worker and check their token between steps; a worker keeps its scratch
// buffers while it has work and lets them go when the queue runs dry. -xlinka
public static class VariantWorkers
{
    private const int Queued = 0;
    private const int Started = 1;
    private const int Finished = 2;

    private sealed class Job
    {
        public required Action<CancellationToken> Work;
        public required Action<Exception?> Done;
        public required string Label;
        public CancellationTokenSource? Linked;
        public CancellationToken Token;
        public CancellationTokenRegistration Registration;
        public int State;
    }

    private static readonly object _lock = new();
    private static readonly Queue<Job> _urgent = new();
    private static readonly Queue<Job> _background = new();
    private static readonly List<Thread> _threads = new();
    // Cancelled when the engine shuts down; every job's token is tied to the one current when it was queued.
    private static CancellationTokenSource _lifetime = new();
    private static int _running;

    [ThreadStatic]
    private static bool _onWorker;

    [ThreadStatic]
    private static byte[]? _scratchA;

    [ThreadStatic]
    private static byte[]? _scratchB;

    public static int Count => Engine.UseConstrainedResources ? 1 : System.Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

    public static Task Run(string label, Action<CancellationToken> work, bool urgent = false, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(label, work, urgent, ct, error =>
        {
            if (error == null)
                done.TrySetResult();
            else if (error is OperationCanceledException)
                done.TrySetCanceled();
            else
                done.TrySetException(error);
        });
        return done.Task;
    }

    public static Task<T> Run<T>(string label, Func<CancellationToken, T> work, bool urgent = false, CancellationToken ct = default)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        T result = default!;
        Enqueue(label, token => result = work(token), urgent, ct, error =>
        {
            if (error == null)
                done.TrySetResult(result);
            else if (error is OperationCanceledException)
                done.TrySetCanceled();
            else
                done.TrySetException(error);
        });
        return done.Task;
    }

    // The worker's own buffer of at least length bytes, kept for its next job. Two of them, for a job that
    // needs a source and a destination at once. Contents are whatever the last job left. Off a worker it is
    // just a new array.
    public static byte[] Scratch(int which, int length)
    {
        if (!_onWorker)
            return new byte[length];
        ref byte[]? slot = ref which == 0 ? ref _scratchA : ref _scratchB;
        if (slot == null || slot.Length < length)
            slot = new byte[System.Math.Max(length, (int)System.Math.Min(int.MaxValue, (slot?.LongLength ?? 0) * 3 / 2))];
        return slot;
    }

    // Engine shutdown: every queued job is answered cancelled, running ones see their token at the next step.
    // Whatever they leave behind is whole, since every variant file is written beside its address and moved in.
    public static void Shutdown()
    {
        Interlocked.Exchange(ref _lifetime, new CancellationTokenSource()).Cancel();
    }

    private static void Enqueue(string label, Action<CancellationToken> work, bool urgent, CancellationToken ct, Action<Exception?> done)
    {
        var job = new Job { Work = work, Done = done, Label = label };
        var lifetime = Volatile.Read(ref _lifetime).Token;
        job.Linked = ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime) : null;
        job.Token = job.Linked?.Token ?? lifetime;
        // Before it is queued, so no worker can finish it first; an already cancelled token runs this at once.
        job.Registration = job.Token.UnsafeRegister(static state =>
        {
            var cancelled = (Job)state!;
            if (Interlocked.CompareExchange(ref cancelled.State, Finished, Queued) == Queued)
                ThreadPool.UnsafeQueueUserWorkItem(static j => Finish(j, new OperationCanceledException(j.Token)), cancelled, preferLocal: false);
        }, job);
        lock (_lock)
        {
            (urgent ? _urgent : _background).Enqueue(job);
            if (_threads.Count < Count && _running + _urgent.Count + _background.Count > _threads.Count)
                StartThread();
            Monitor.Pulse(_lock);
        }
    }

    private static void Finish(Job job, Exception? error)
    {
        job.Registration.Dispose();
        job.Linked?.Dispose();
        job.Done(error);
    }

    private static void StartThread()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = $"Variant worker {_threads.Count + 1}",
            Priority = ThreadPriority.BelowNormal,
        };
        _threads.Add(thread);
        thread.Start();
    }

    private static void Loop()
    {
        _onWorker = true;
        while (true)
        {
            Job job;
            lock (_lock)
            {
                while (_urgent.Count == 0 && _background.Count == 0)
                {
                    // Idle: the buffers are the biggest thing a worker holds, and nothing is coming for them.
                    _scratchA = null;
                    _scratchB = null;
                    Monitor.Wait(_lock);
                }
                job = _urgent.Count > 0 ? _urgent.Dequeue() : _background.Dequeue();
                // Cancelled while it waited: already answered.
                if (Interlocked.CompareExchange(ref job.State, Started, Queued) != Queued)
                    continue;
                _running++;
            }

            Exception? error = null;
            try
            {
                job.Token.ThrowIfCancellationRequested();
                job.Work(job.Token);
            }
            catch (Exception ex)
            {
                error = ex;
                if (ex is not OperationCanceledException)
                    Logger.Warn($"VariantWorkers: {job.Label} failed: {ex.Message}");
            }
            lock (_lock)
                _running--;
            Volatile.Write(ref job.State, Finished);
            Finish(job, error);
        }
    }
}
