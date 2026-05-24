using System;
using System.Collections.Generic;
using Lumora.Core.Networking.Streams;
using Lumora.Core.Scheduling;
namespace Lumora.Core.Networking.Session;

public class SessionRawStreamManager : IDisposable
{
    private readonly Object _streamlock = new();
    private readonly List<IRawStream> ActiveStreams = new();
    private readonly Dictionary<uint, AsyncDisposibleLockedTimer> Timers = new();
    private readonly Session _session;
    private ReferenceController _referenceController;
    public SessionRawStreamManager(Session parent)
    {
        _session = parent;
        parent.RawFrameReceived += OnMessage;
        _referenceController = parent.World.ReferenceController;
    }

    private void OnMessage(User sender, RefID streamRefID, ushort sequence, ReadOnlyMemory<byte> payload)
    {
        if (_referenceController.TryGetObject<Stream>(streamRefID, out var stream)
        || stream.Owner != sender || stream is not IRawStream raw || payload.Length > NetworkLimits.MaxRawFrameBytes)
            return;
        raw.EnqueueRawFrame(sequence, payload);
    }
    public void StartPolling(IRawStream stream)
    {
        lock (_streamlock)
        {
            ActiveStreams.Add(stream);
            uint rate = stream.PollingRate;
            AsyncDisposibleLockedTimer thistimer;
            if (!Timers.TryGetValue(rate, out thistimer))
            {
                thistimer = new(TimeSpan.FromMilliseconds(stream.PollingRate));
                thistimer.Start();
                Timers.Add(rate, thistimer);
            }
            thistimer.Add(stream.Poll);
        }
    }
    public void StopPolling(IRawStream stream)
    {
        lock (_streamlock)
        {
            uint rate = stream.PollingRate;
            ActiveStreams.Remove(stream);
            if (Timers.TryGetValue(rate, out var timer))
            {
                timer.Remove(stream.Poll);
                if (timer.GetRefCount() < 1)
                {
                    Timers.Remove(rate);
                    timer.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        foreach (var v in Timers)
        {
            v.Value.Dispose();
        }
    }
}