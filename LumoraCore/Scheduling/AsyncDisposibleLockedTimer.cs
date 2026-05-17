using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Core.Scheduling;

public class AsyncDisposibleLockedTimer : IDisposable
{
    private readonly PeriodicTimer _timer;
    private readonly List<Func<Task>> users = new();
    private readonly CancellationTokenSource cancellationToken = new();
    private readonly Object _lock = new();
    public Task currentPoll {get; private set;} = Task.CompletedTask;
    public AsyncDisposibleLockedTimer(TimeSpan defaulttime,CancellationToken token)
    {
        _timer = new(defaulttime);
        Task.Run(Run);
    }
    private async void Run()
    {
        while(true){
            var res = await _timer.WaitForNextTickAsync(cancellationToken.Token);
            if(!res)break;
            List<Task> tasks = new();
            lock (_lock)
            {
                foreach(Func<Task> task in users)
                {
                    tasks.Add(task());
                }
            }
            currentPoll = Task.WhenAll(tasks);
        }
    }
    public void Add(Func<Task> act)
    {
        lock(_lock)
        users.Add(act);
    }
    public void Remove(Func<Task> act)
    {
        lock(_lock)
        users.Remove(act);
    }
    public int GetRefCount()
    {
        lock(_lock)
        return users.Count;
    }
    public void Dispose()
    {
        cancellationToken.Cancel();
        _timer.Dispose();
    }
}