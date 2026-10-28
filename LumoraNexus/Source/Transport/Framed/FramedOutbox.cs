// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.Nexus.Transport.Framed;

// What one stream still has to write, by lane. Control frames go first. The lanes then take turns, each a
// quantum of bytes per turn (deficit round robin), so a backlog on one never shuts another out: foreground and
// unreliable get the most, bulk a fair share, background the least. Unreliable messages wait under a budget of
// their own and are dropped, oldest first, rather than queued behind a slow link, and one that sat too long is
// dropped unsent: a pose that late is worse than none. A message already started always finishes. -xlinka
internal sealed class FramedOutbox
{
    private const int UnreliableBudget = 256 * 1024;
    private const long UnreliableMaxAgeMs = 750;
    private const int SmallestUsefulFragment = 1024;

    private sealed class Message
    {
        public byte[] Data = null!;
        public int Count;
        public int Offset;
        public bool Pooled;
        public long StampMs;
    }

    private sealed class Lane
    {
        public byte Id;
        public int Quantum;
        public int Deficit;
        public bool Visiting;
        public Message? Current;
        public readonly Queue<Message> Waiting = new();
    }

    private readonly object _gate = new();
    private readonly Queue<byte[]> _control = new();
    private readonly Lane[] _order;
    private readonly Lane?[] _byId = new Lane?[FramedWire.MaxLanes];
    private readonly SemaphoreSlim _signal = new(0);
    private int _signaled;
    private int _turn;
    private long _pendingBytes;
    private int _unreliableBytes;
    private int _queued;
    private long _frames;
    private long _dropped;
    private bool _sealed;

    public FramedOutbox(IReadOnlyList<byte> lanes)
    {
        var order = new List<Lane>();
        foreach (byte id in new[] { FramedWire.LaneUnreliable, FramedWire.LaneForeground, FramedWire.LaneBackground })
        {
            if (Contains(lanes, id))
                order.Add(_byId[id] = new Lane { Id = id, Quantum = Quantum(id) });
        }
        foreach (byte id in lanes)
        {
            if (id >= FramedWire.FirstBulkLane && id < FramedWire.MaxLanes && _byId[id] == null)
                order.Add(_byId[id] = new Lane { Id = id, Quantum = Quantum(id) });
        }
        _order = order.ToArray();
    }

    private static bool Contains(IReadOnlyList<byte> lanes, byte id)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i] == id)
                return true;
        }
        return false;
    }

    private static int Quantum(byte lane) => lane switch
    {
        FramedWire.LaneUnreliable => 32 * 1024,
        FramedWire.LaneForeground => 32 * 1024,
        FramedWire.LaneBackground => 12 * 1024,
        _ => 24 * 1024,
    };

    private static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    public int Queued => Volatile.Read(ref _queued);

    public long Frames => Interlocked.Read(ref _frames);

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool Sealed
    {
        get
        {
            lock (_gate)
                return _sealed;
        }
    }

    public bool Carries(byte lane) => lane < FramedWire.MaxLanes && _byId[lane] != null;

    // Takes the buffer over: a pooled one goes back to the pool once written or dropped.
    public bool Enqueue(byte lane, byte[] data, int count, bool pooled)
    {
        var target = lane < FramedWire.MaxLanes ? _byId[lane] : null;
        lock (_gate)
        {
            if (_sealed || target == null)
            {
                if (pooled)
                    ArrayPool<byte>.Shared.Return(data);
                return false;
            }
            if (lane == FramedWire.LaneUnreliable)
            {
                while (_unreliableBytes + count > UnreliableBudget && target.Waiting.Count > 0)
                    Drop(target.Waiting.Dequeue());
                _unreliableBytes += count;
            }
            target.Waiting.Enqueue(new Message { Data = data, Count = count, Pooled = pooled, StampMs = NowMs });
            _pendingBytes += count;
            _queued++;
        }
        Wake();
        return true;
    }

    public void EnqueueControl(byte[] frame)
    {
        lock (_gate)
        {
            if (_sealed)
                return;
            _control.Enqueue(frame);
        }
        Wake();
    }

    // The last control frame (a close) goes out; data still waiting is dropped.
    public void Seal(byte[]? last)
    {
        lock (_gate)
        {
            if (_sealed)
                return;
            _sealed = true;
            foreach (var lane in _order)
            {
                if (lane.Current != null)
                    Release(lane.Current);
                lane.Current = null;
                while (lane.Waiting.Count > 0)
                    Release(lane.Waiting.Dequeue());
            }
            _pendingBytes = 0;
            _unreliableBytes = 0;
            _queued = 0;
            if (last != null)
                _control.Enqueue(last);
        }
        Wake();
    }

    public bool Drained
    {
        get
        {
            lock (_gate)
                return _sealed && _control.Count == 0;
        }
    }

    public void Wake()
    {
        if (Interlocked.Exchange(ref _signaled, 1) == 0)
            _signal.Release();
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _signal.WaitAsync(cancellationToken);

    // Fills the batch with frames to write next; 0 when there is nothing. Control frames always go; data only up to
    // the budget.
    public int Take(byte[] batch, long budget = long.MaxValue)
    {
        Volatile.Write(ref _signaled, 0);
        lock (_gate)
        {
            int written = 0;
            while (_control.Count > 0 && written + _control.Peek().Length <= batch.Length)
            {
                var frame = _control.Dequeue();
                Buffer.BlockCopy(frame, 0, batch, written, frame.Length);
                written += frame.Length;
                _frames++;
            }
            if (_control.Count > 0 || _sealed)
                return written;
            int end = budget >= batch.Length - written ? batch.Length : written + (int)Math.Max(0, budget);

            int idle = 0;
            while (idle < _order.Length)
            {
                var lane = _order[_turn];
                if (!Ready(lane))
                {
                    lane.Deficit = 0;
                    lane.Visiting = false;
                    _turn = (_turn + 1) % _order.Length;
                    idle++;
                    continue;
                }
                if (!lane.Visiting)
                {
                    lane.Deficit += lane.Quantum;
                    lane.Visiting = true;
                }
                while (lane.Deficit > 0 && Ready(lane))
                {
                    var message = lane.Current!;
                    int room = end - written - FramedWire.HeaderBytes;
                    if (room < Math.Min(message.Count - message.Offset, SmallestUsefulFragment) || room <= 0)
                        return written;
                    lane.Deficit -= WriteFragment(lane, batch, ref written, room);
                }
                if (lane.Current == null && lane.Waiting.Count == 0)
                    lane.Deficit = 0;
                lane.Visiting = false;
                _turn = (_turn + 1) % _order.Length;
                idle = 0;
            }
            return written;
        }
    }

    private bool Ready(Lane lane)
    {
        if (lane.Current != null)
            return true;
        long now = lane.Id == FramedWire.LaneUnreliable ? NowMs : 0;
        while (lane.Waiting.Count > 0)
        {
            var next = lane.Waiting.Dequeue();
            if (lane.Id == FramedWire.LaneUnreliable && now - next.StampMs > UnreliableMaxAgeMs)
            {
                Drop(next);
                continue;
            }
            lane.Current = next;
            return true;
        }
        return false;
    }

    private int WriteFragment(Lane lane, byte[] batch, ref int written, int room)
    {
        var message = lane.Current!;
        int remaining = message.Count - message.Offset;
        int take = Math.Min(Math.Min(remaining, FramedWire.MaxFragment), room);
        bool last = take == remaining;
        FramedWire.WriteHeader(batch.AsSpan(written), last ? FramedWire.KindLast : FramedWire.KindPart, lane.Id, take);
        Buffer.BlockCopy(message.Data, message.Offset, batch, written + FramedWire.HeaderBytes, take);
        written += FramedWire.HeaderBytes + take;
        message.Offset += take;
        _pendingBytes -= take;
        _frames++;
        if (last)
        {
            lane.Current = null;
            if (lane.Id == FramedWire.LaneUnreliable)
                _unreliableBytes -= message.Count;
            _queued--;
            Release(message);
        }
        return FramedWire.HeaderBytes + take;
    }

    private void Drop(Message message)
    {
        _pendingBytes -= message.Count - message.Offset;
        _unreliableBytes -= message.Count;
        _queued--;
        _dropped++;
        Release(message);
    }

    private static void Release(Message message)
    {
        if (message.Pooled)
            ArrayPool<byte>.Shared.Return(message.Data);
        message.Data = Array.Empty<byte>();
        message.Pooled = false;
    }
}
