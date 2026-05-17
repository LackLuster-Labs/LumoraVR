using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Streams;
using System.Timers;
using Lumora.Core.Scheduling;
using System.Threading;
namespace Lumora.Core.Networking.Session;

public class SessionRawStreamManager : IDisposable
{
    private readonly List<IRawStream> activestreams = new();
    private readonly Dictionary<uint, AsyncDisposibleLockedTimer> timers = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Session _session;
    public SessionRawStreamManager(Session parent)
    {
        _session = parent;
        parent.RawFrameReceived += OnMessage;
    }

    private void OnMessage(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload)
    {
        if (!_session.World.ReferenceController.TryGetObject<Stream>(streamRefID, out var stream)
        || stream.Owner != sender || stream is not IRawStream raw)
            return;
        raw.EnqueueRawFrame(sequence, payload);
    }
    public void StartPolling(IRawStream stream)
    {
        activestreams.Add(stream);
        uint rate = stream.PollingRate;
        AsyncDisposibleLockedTimer thistimer;
        if (!timers.TryGetValue(rate, out thistimer))
        {
            thistimer = new(TimeSpan.FromMilliseconds(stream.PollingRate), _cancellation.Token);
            timers.Add(rate, thistimer);
        }
        thistimer.Add(stream.Poll);
    }
    public void StopPolling(IRawStream stream)
    {
        uint rate = stream.PollingRate;
        activestreams.Remove(stream);
        if (timers.TryGetValue(rate, out var timer))
        {
            timer.Remove(stream.Poll);
            if (timer.GetRefCount() < 1) timers.Remove(rate);
            timer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var v in timers)
        {
            v.Value.Dispose();
        }
    }
}