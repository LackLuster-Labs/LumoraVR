using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumora.Core.Logging;

namespace Lumora.Core.Scheduling;

public class AsyncDisposibleLockedTimer : IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly List<Func<Task>> users = new();
    private readonly CancellationTokenSource cancellationToken = new();
    private readonly Object _lock = new();
    public delegate void ErrorHandler(Task task);
    public event ErrorHandler OnError;
    private uint _trys = 0;
    public AsyncDisposibleLockedTimer(TimeSpan defaulttime)
    {
        _timer = new(defaulttime);
    }
    private void EndHendler(Task result)
    {
        if (cancellationToken.Token.IsCancellationRequested) return;
        if (result.IsFaulted)
        {
            Logger.Log($"[AsyncDisposibleLockedTimer] fault with ex: {result.Exception}");

        }
        else if (!result.IsCompleted)
        {
            Logger.Log($"[AsyncDisposibleLockedTimer] was stoped restarting");
        }
        if (_trys <= 5 && !cancellationToken.Token.IsCancellationRequested)
        {
            Start();
        }
        else
            Logger.Log($"[AsyncDisposibleLockedTimer] Is out of retrys");
    }
    public void Start()
    {
        _trys = 0;
        var result = Task.Run(Run);
        result.ContinueWith(EndHendler);
    }
    private async Task PollErrorHandler(IReadOnlyList<Task> tasks)
    {
        var exceptions = from task in tasks where task.IsFaulted select task;
        if (exceptions is null) return;
        foreach (var task in exceptions)
        {
            OnError.Invoke(task);
        }
    }
    private async Task Run()
    {
        while (true)
        {
            var res = await _timer.WaitForNextTickAsync(cancellationToken.Token);
            if (!res) break;
            List<Task> tasks = new();
            lock (_lock)
            {
                foreach (Func<Task> task in users)
                {
                    tasks.Add(task());
                }
            }
            _ = Task.WhenAll(tasks).ContinueWith(async (task) => { if (task.IsFaulted) await PollErrorHandler(tasks.AsReadOnly()); });
        }
    }
    public void Add(Func<Task> act)
    {
        lock (_lock)
            users.Add(act);
    }
    public void Remove(Func<Task> act)
    {
        lock (_lock)
            users.Remove(act);
    }
    public int GetRefCount()
    {
        lock (_lock)
            return users.Count;
    }
    public void Dispose()
    {
        cancellationToken.Cancel();
        cancellationToken.Dispose();
        _timer.Dispose();
    }
}