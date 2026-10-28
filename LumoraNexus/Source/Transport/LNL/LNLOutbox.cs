// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteNetLib;

namespace Lumora.Nexus.Transport.LNL;

// What one LNL peer is sent, held to what its link carries (LNLPace). Everything is already encrypted when it
// gets here; nothing about the wire changes, LNL just gets it no faster than the link takes it.
//
// Live data (voice, poses, world changes, anything that is not bulk) goes the moment it is handed over. It still
// spends the link's rate, and only a flood past a quarter second of debt waits; under that the oldest waiting
// unreliable message goes, never a queue that grows. Bulk gets what live leaves: it waits until the rate has paid
// for everything sent before it, so it goes out spread at the link's own pace instead of a window at a time, and
// what LNL holds unacknowledged stays inside the window. Sent a window at a time, each burst stood in the
// bottleneck's queue in front of the next pose and voice frame: 20-50 ms behind a big transfer even on a link
// with room to spare. What counts as on the wire is what each channel can have out: LNL keeps 64 packets in
// flight per channel, so a channel's backlog past that waits in LNL without loading the link, and a message for
// a channel already that deep goes straight through.
//
// Acknowledgements free the window on LNL's receive thread, deep inside its locks, so the outbox is drained from
// there through the thread pool, never inline. Hand-offs to LNL go one at a time, in order, so a channel's
// messages keep the order they were sent in. -xlinka
internal sealed class LNLOutbox
{
    private enum Kind : byte { Unreliable, Foreground, Background, Bulk }

    private sealed class Ticket
    {
        public LNLOutbox Owner = null!;
        public int Bytes;
        public int Slot;
        public LNLPace.Stamp Stamp;
        public int Lane = -1;
        public int Packets;
        public bool Clean;
    }

    private struct Held
    {
        public byte[] Data;
        public int Length;
        public bool Pooled;
        public byte Channel;
        public DeliveryMethod Method;
        public Kind Kind;
    }

    private const int SlotPackets = 64;
    private const double UnreliableQueueSeconds = 0.05;
    private const int MinUnreliableQueue = 16 * 1024;
    // How far live data may run the rate into debt before it waits, and how much an idle link saves up for bulk.
    private const double LiveDebtSeconds = 0.25;
    private const int MinLiveDebt = 256 * 1024;
    private const double BulkBurstSeconds = 0.002;
    // Bulk pays this much more than its size, so it keeps the link a little under what it carries and the
    // bottleneck's queue drains between the pacer's probes instead of standing in front of live data.
    private const double BulkCost = 1.0 / 0.9;
    // Bulk LNL may hold unsent. Its one sending thread puts every channel's packets out in turn, so a pose or a
    // voice frame handed over waits for whatever bulk is ahead of it in that pass; kept to this, a pass is short.
    private const int MaxQueuedBulkPackets = 48;

    private readonly NetPeer _peer;
    private readonly NetManager _manager;
    private readonly LinkEmulator? _emulator;
    private readonly LNLPace _pace = new();
    private readonly LNLSendTally.Counts _tally;
    private readonly LNLLanes _lanes = new();
    private readonly object _wire = new();
    private readonly object _gate = new();
    private readonly Queue<Held> _unreliable = new();
    private readonly Queue<Held> _foreground = new();
    private readonly Queue<Held> _background = new();
    private readonly Queue<Held> _bulk = new();
    private readonly List<(Held Held, Ticket? Ticket)> _handoffs = new();
    private readonly Stack<Ticket> _spareTickets = new();
    private readonly long[] _undelivered = new long[LNLLanes.Channels * 2];
    private long _charged;
    private long _undeliveredTotal;
    private long _unreliableQueued;
    private long _liveQueued;
    private long _bulkQueued;
    // What the rate has paid for and not been spent yet; live sends drive it below zero and bulk waits it back.
    private double _tokens;
    private long _tokensAt;
    private int _bulkParked;
    private double _now;
    private long _sentBytes;
    private long _droppedUnreliable;
    private int _pumpQueued;
    private readonly WaitCallback _pumpCallback;

    public LNLOutbox(NetPeer peer, NetManager manager, LinkEmulator? emulator = null)
    {
        _peer = peer;
        _manager = manager;
        _emulator = emulator;
        _tally = LNLSendTally.For(peer);
        _pumpCallback = _ => { Volatile.Write(ref _pumpQueued, 0); Pump(); };
        _tokensAt = Stopwatch.GetTimestamp();
    }

    public ulong SentBytes => (ulong)Interlocked.Read(ref _sentBytes);

    public long DroppedUnreliable => Interlocked.Read(ref _droppedUnreliable);

    // Reliable packets put on the wire, resends among them, and how many of those were resends.
    public long ReliablePackets => _tally.Sent;

    public long Resent => _tally.Resent;

    // What waits here, every kind, in packets.
    public int HeldPackets
        => (int)Math.Min(int.MaxValue, (Interlocked.Read(ref _liveQueued) + Interlocked.Read(ref _bulkQueued) + LNLLanes.PacketBytes - 1) / LNLLanes.PacketBytes);

    // What the next live message waits behind: live data held here and what LNL holds unsent on the live
    // channels. Bulk is left out; it is paced under live data and holds none of it up.
    public long LiveBacklogBytes
        => Interlocked.Read(ref _liveQueued) + (long)LNLLanes.QueuedLive(_peer) * LNLLanes.PacketBytes + (_emulator?.QueuedBytes ?? 0);

    public void Send(byte[] data, int length, bool reliable, bool background)
    {
        byte channel = (byte)(background ? 1 : 0);
        if (!reliable && _peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced) >= length)
        {
            Pump(new Held { Data = data, Length = length, Channel = channel, Method = DeliveryMethod.Sequenced, Kind = Kind.Unreliable });
            return;
        }
        // Unreliable data too big for one packet has always gone reliable and unordered.
        var method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.ReliableUnordered;
        Pump(new Held { Data = data, Length = length, Channel = channel, Method = method, Kind = background ? Kind.Background : Kind.Foreground });
    }

    public void SendBulk(byte[] data, int length)
        => Pump(new Held { Data = data, Length = length, Kind = Kind.Bulk });

    // From LNL's delivery callback, for every reliable send the outbox made.
    public static void Delivered(object? userData)
    {
        if (userData is Ticket ticket)
            ticket.Owner.OnDelivered(ticket);
    }

    // Once a frame at least, so unreliable data waiting on its budget leaves when the budget comes back.
    public void Pump() => Pump(null);

    private void Pump(Held? incoming)
    {
        if (_peer.ConnectionState != ConnectionState.Connected)
            return;
        bool sent;
        lock (_wire)
        {
            lock (_gate)
            {
                _now = NowMs();
                // Until a small message has come back, LNL's own ping is the round trip.
                if (_pace.MinRttMs < 0)
                    _pace.SeedRtt(_peer.RoundTripTime, _now);
                var stats = _peer.Statistics;
                _pace.Tick(_now, stats.BytesSent, stats.PacketsReceived, _undeliveredTotal - _charged);
                Refill();
                if (incoming is { } held)
                    Accept(held);
                Drain();
            }
            sent = _handoffs.Count > 0;
            foreach (var (held, ticket) in _handoffs)
                HandOff(held, ticket);
            _handoffs.Clear();
        }
        if (sent)
            _manager.TriggerUpdate();
    }

    // The new message is handed on at once where nothing of its kind waits and the budget has room; otherwise it
    // is copied and waits its turn.
    private void Accept(Held held)
    {
        var queue = QueueFor(held.Kind);
        if (queue.Count == 0 && TryTake(ref held))
            return;
        var copy = ArrayPool<byte>.Shared.Rent(held.Length);
        Buffer.BlockCopy(held.Data, 0, copy, 0, held.Length);
        held.Data = copy;
        held.Pooled = true;
        queue.Enqueue(held);
        if (held.Kind == Kind.Bulk)
        {
            Interlocked.Add(ref _bulkQueued, held.Length);
            return;
        }
        Interlocked.Add(ref _liveQueued, held.Length);
        if (held.Kind != Kind.Unreliable)
            return;
        _unreliableQueued += held.Length;
        // The oldest goes first: a pose or a voice frame that waited this long is already stale.
        double limit = Math.Max(MinUnreliableQueue, _pace.SendRate * UnreliableQueueSeconds);
        while (_unreliableQueued > limit && _unreliable.Count > 1)
        {
            var old = _unreliable.Dequeue();
            _unreliableQueued -= old.Length;
            Interlocked.Add(ref _liveQueued, -old.Length);
            Interlocked.Increment(ref _droppedUnreliable);
            ArrayPool<byte>.Shared.Return(old.Data);
        }
    }

    private void Drain()
    {
        DrainQueue(_unreliable);
        DrainQueue(_foreground);
        DrainQueue(_background);
        DrainQueue(_bulk);
        if (_unreliable.Count > 0 || _foreground.Count > 0 || _background.Count > 0 || _bulk.Count > 0
            || _charged * 5 >= _pace.Window * 4)
            _pace.MarkBusy();
        // Bulk held back for the rate or for LNL's own queue may have no acknowledgement coming to send it on, so
        // the waker does.
        if (_bulk.Count > 0 && Interlocked.Exchange(ref _bulkParked, 1) == 0)
            Waker.Park(this);
    }

    private void DrainQueue(Queue<Held> queue)
    {
        while (queue.Count > 0)
        {
            var held = queue.Peek();
            if (!TryTake(ref held))
                return;
            queue.Dequeue();
            if (held.Kind == Kind.Bulk)
            {
                Interlocked.Add(ref _bulkQueued, -held.Length);
                continue;
            }
            Interlocked.Add(ref _liveQueued, -held.Length);
            if (held.Kind == Kind.Unreliable)
                _unreliableQueued -= held.Length;
        }
    }

    // Spends the budget the message needs and lines it up for LNL, or leaves it where it is.
    private bool TryTake(ref Held held)
    {
        bool live = held.Kind != Kind.Bulk;
        if (live ? _tokens <= -Math.Max(MinLiveDebt, _pace.SendRate * LiveDebtSeconds) : _tokens <= 0)
            return false;
        if (held.Kind == Kind.Unreliable)
        {
            _tokens -= held.Length;
            _pace.OnUnreliableSent(held.Length, _now);
            _handoffs.Add((held, null));
            return true;
        }

        int lane = -1, packets = 0;
        if (held.Kind == Kind.Bulk)
        {
            if (LNLLanes.QueuedBulk(_peer) > MaxQueuedBulkPackets)
                return false;
            lane = _lanes.Choose(_peer, held.Length, out held.Channel, out held.Method, out packets);
        }
        int slot = held.Channel * 2 + (held.Method == DeliveryMethod.ReliableOrdered ? 1 : 0);
        long cap = (long)SlotPackets * LNLLanes.PacketBytes;
        long before = Math.Min(_undelivered[slot], cap);
        long after = Math.Min(_undelivered[slot] + held.Length, cap);
        // Bulk stays inside the window, live counted in it; a message bigger than the whole window still goes on
        // its own. Live is not held to it at all.
        if (!live && after > before && _charged > 0 && _charged + (after - before) > _pace.Window)
        {
            if (lane >= 0)
                _lanes.Release(lane, packets);
            return false;
        }
        _tokens -= live ? held.Length : held.Length * BulkCost;
        bool clean = _undelivered[slot] == 0 && held.Length <= cap;
        _undelivered[slot] += held.Length;
        _undeliveredTotal += held.Length;
        _charged += after - before;
        var ticket = _spareTickets.Count > 0 ? _spareTickets.Pop() : new Ticket { Owner = this };
        ticket.Bytes = held.Length;
        ticket.Slot = slot;
        ticket.Lane = lane;
        ticket.Packets = packets;
        ticket.Clean = clean;
        ticket.Stamp = _pace.OnSent(held.Length, _now);
        _handoffs.Add((held, ticket));
        return true;
    }

    private void HandOff(Held held, Ticket? ticket)
    {
        Interlocked.Add(ref _sentBytes, held.Length);
        if (_emulator != null)
        {
            var copy = held.Data.AsSpan(0, held.Length).ToArray();
            if (held.Pooled)
                ArrayPool<byte>.Shared.Return(held.Data);
            _emulator.Send(copy.Length, droppable: ticket == null, () =>
            {
                if (_peer.ConnectionState != ConnectionState.Connected)
                    return;
                Write(copy, copy.Length, held.Channel, held.Method, ticket);
                _manager.TriggerUpdate();
            });
            return;
        }
        Write(held.Data, held.Length, held.Channel, held.Method, ticket);
        if (held.Pooled)
            ArrayPool<byte>.Shared.Return(held.Data);
    }

    private void Write(byte[] data, int length, byte channel, DeliveryMethod method, Ticket? ticket)
    {
        if (ticket == null)
        {
            _peer.Send(data, 0, length, channel, method);
            return;
        }
        _peer.SendWithDeliveryEvent(data, 0, length, channel, method, ticket);
    }

    private void OnDelivered(Ticket ticket)
    {
        lock (_gate)
        {
            long cap = (long)SlotPackets * LNLLanes.PacketBytes;
            long before = Math.Min(_undelivered[ticket.Slot], cap);
            _undeliveredTotal -= Math.Min(_undelivered[ticket.Slot], ticket.Bytes);
            _undelivered[ticket.Slot] = Math.Max(0, _undelivered[ticket.Slot] - ticket.Bytes);
            long after = Math.Min(_undelivered[ticket.Slot], cap);
            _charged = Math.Max(0, _charged - (before - after));
            _pace.OnDelivered(ticket.Stamp, ticket.Bytes, ticket.Clean, NowMs());
            if (ticket.Lane >= 0)
                _lanes.Release(ticket.Lane, ticket.Packets);
            ticket.Lane = -1;
            _spareTickets.Push(ticket);
        }
        // LNL calls this inside its own locks: the window it freed is spent from the pool, never from here.
        if (Interlocked.Exchange(ref _pumpQueued, 1) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(_pumpCallback, null);
    }

    private void Refill()
    {
        long now = Stopwatch.GetTimestamp();
        double rate = _pace.SendRate;
        _tokens += rate * (now - _tokensAt) / Stopwatch.Frequency;
        _tokensAt = now;
        double burst = rate * BulkBurstSeconds;
        // A queued message keeps the credit earned while the waker was asleep, up to one message's cost.
        // A 1 ms sleep can take a whole scheduler tick: capping its credit at 2 ms discards the rest on
        // every send and makes both throughput and the next bandwidth probe read artificially low.
        // An idle link still saves only the small burst above, and bulk still pays its full cost.
        if (_bulk.TryPeek(out var next))
            burst = Math.Max(burst, next.Length * BulkCost);
        if (_tokens > burst)
            _tokens = burst;
    }

    // Pumps outboxes whose bulk is waiting, every millisecond, from one thread for all of them.
    private static class Waker
    {
        private static readonly System.Collections.Concurrent.ConcurrentQueue<LNLOutbox> _parked = new();
        private static readonly AutoResetEvent _signal = new(false);

        static Waker()
        {
            new Thread(Run) { Name = "LNLBulkPace", IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
        }

        public static void Park(LNLOutbox outbox)
        {
            _parked.Enqueue(outbox);
            _signal.Set();
        }

        private static void Run()
        {
            var due = new List<LNLOutbox>();
            while (true)
            {
                if (_parked.IsEmpty)
                    _signal.WaitOne();
                Thread.Sleep(1);
                while (_parked.TryDequeue(out var outbox))
                    due.Add(outbox);
                foreach (var outbox in due)
                {
                    Volatile.Write(ref outbox._bulkParked, 0);
                    try { outbox.Pump(); }
                    catch { }
                }
                due.Clear();
            }
        }
    }

    private Queue<Held> QueueFor(Kind kind) => kind switch
    {
        Kind.Unreliable => _unreliable,
        Kind.Foreground => _foreground,
        Kind.Background => _background,
        _ => _bulk,
    };

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}
