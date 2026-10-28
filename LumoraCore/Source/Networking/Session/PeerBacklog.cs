// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Diagnostics;

namespace Lumora.Core.Networking.Session;

// One peer's link as the authority's sync loop sees it: how far behind it is, and the values it was not sent while
// behind. A link that could not drain what the host made (a thin uplink, a relay, thousands of moving bodies)
// took every delta anyway, so its queue only grew: the peer saw the world later and later and, over a transport
// with a bounded send buffer, was dropped every few seconds. A peer that is behind now gets what has to arrive in
// order, structure and one-off edits, and owes the rest: values that change every few cycles, of which only the
// newest matters. Those go out as the link makes room, newest value, longest owed first. -xlinka
internal sealed class PeerBacklog
{
    // Behind once this much waits for the wire, or this long at the rate the link drains if that is more.
    private const long BehindFloorBytes = 96 * 1024;
    private const double BehindSeconds = 0.12;
    // Caught up again under this, with nothing owed.
    private const long ClearFloorBytes = 24 * 1024;
    private const double ClearSeconds = 0.04;
    // The least a cycle sends of what is owed once the queue is under the clear mark, so a link whose rate is
    // not known yet still gets there.
    private const long MinFlushBytes = 8 * 1024;
    // While behind, what is owed still goes out at a share of what the link drains, whatever waits in its queue:
    // kept only to the room between the marks, a link held over them by changes that must arrive (a spawn, edits,
    // everyone's voice) sent no moving value at all and everything in the world stood still for as long as that
    // lasted, sixteen seconds and counting on a 1.5 MB/s uplink. The share grows with how long the oldest owed value
    // has waited, so a moving thing on a thin link is late and coarse but never frozen. -xlinka
    private const double MovingShare = 0.2;
    private const double MovingShareMax = 0.6;
    private const double OwedSecondsForMax = 1.0;
    private const long MinMovingBytes = 1024;

    private readonly Queue<(RefID Id, long Since)> _order = new();
    private readonly HashSet<RefID> _owed = new();
    private ulong _lastSent;
    private long _lastPending;
    private long _lastStamp;
    private double _drainRate;

    public bool Behind { get; private set; }
    public long Pending { get; private set; }
    public long PeakPending { get; private set; }
    public double DrainRate => _drainRate;
    public int OwedCount => _owed.Count;
    public int TimesBehind { get; private set; }

    // This cycle's share of FlushRoom still unspent, and whether a numbered delta has reached the peer.
    public long CycleRoom { get; set; }
    public bool Served { get; set; }

    // So much waits that more on top only ages in the queue.
    public bool Full => Pending > BehindAt;

    private long BehindAt => System.Math.Max(BehindFloorBytes, (long)(_drainRate * BehindSeconds));
    private long ClearAt => System.Math.Max(ClearFloorBytes, (long)(_drainRate * ClearSeconds));

    // Bytes of owed values this cycle may add: the queue is kept between the two marks.
    public long FlushRoom
    {
        get
        {
            long target = (BehindAt + ClearAt) / 2;
            long room = target - Pending;
            if (Pending < ClearAt)
                room = System.Math.Max(room, MinFlushBytes);
            return System.Math.Max(0, room);
        }
    }

    // sent: every byte handed to the link so far. pending: what of that has not left yet.
    public void Update(ulong sent, long pending, long timestamp)
    {
        double cycleSeconds = 0;
        if (_lastStamp != 0 && timestamp > _lastStamp)
        {
            double seconds = (timestamp - _lastStamp) / (double)Stopwatch.Frequency;
            cycleSeconds = System.Math.Min(seconds, 0.1);
            long drained = (long)(sent - _lastSent) - (pending - _lastPending);
            // A link with nothing waiting only drains what it is given, which says nothing of what it could take.
            if (seconds > 0 && (pending > 0 || _lastPending > 0))
            {
                double rate = System.Math.Max(0, drained) / seconds;
                _drainRate = _drainRate <= 0 ? rate : _drainRate * 0.9 + rate * 0.1;
            }
        }
        _lastSent = sent;
        _lastPending = pending;
        _lastStamp = timestamp;

        Pending = pending;
        if (pending > PeakPending)
            PeakPending = pending;
        if (!Behind && pending > BehindAt)
        {
            Behind = true;
            TimesBehind++;
        }
        else if (Behind && pending < ClearAt && _owed.Count == 0)
        {
            Behind = false;
        }
        CycleRoom = FlushRoom;
        if (Behind && _owed.Count > 0)
        {
            double waited = OldestOwedSeconds(timestamp);
            double share = MovingShare + (MovingShareMax - MovingShare) * System.Math.Clamp(waited / OwedSecondsForMax, 0, 1);
            CycleRoom = System.Math.Max(CycleRoom, System.Math.Max(MinMovingBytes, (long)(_drainRate * cycleSeconds * share)));
        }
        Served = false;
    }

    public void Owe(RefID id)
    {
        if (_owed.Add(id))
            _order.Enqueue((id, Stopwatch.GetTimestamp()));
    }

    // The peer holds something newer than the host could send it: it made the change itself.
    public void Forgive(RefID id) => _owed.Remove(id);

    public bool TryTakeOwed(out RefID id)
    {
        while (_order.Count > 0)
        {
            id = _order.Dequeue().Id;
            if (_owed.Remove(id))
                return true;
        }
        id = default;
        return false;
    }

    // How long the value owed longest has waited.
    private double OldestOwedSeconds(long timestamp)
    {
        while (_order.Count > 0 && !_owed.Contains(_order.Peek().Id))
            _order.Dequeue();
        return _order.Count > 0 ? System.Math.Max(0, timestamp - _order.Peek().Since) / (double)Stopwatch.Frequency : 0;
    }
}
