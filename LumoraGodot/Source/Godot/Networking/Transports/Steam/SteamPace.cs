// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.
#if GODOT_PC

using System;

namespace Lumora.Godot.Networking.Transports.Steam;

// The send rate of one peer's link, both channels together, kept apart from every Steamworks type so it can be
// checked on its own. Steam sends at exactly the rate it is given and never finds one itself (a rate between a
// min and a max just sits at the min), so a fixed rate above what the link takes floods it: the bottleneck
// drops most of what arrives, reliable data goes again and again, and the world's changes and voice wait behind
// the flood. Through an emulated 5 MB/s uplink a fixed 16 + 64 MB/s got 2 MB/s across, live changes 340 ms late at p95.
// The rate here follows what gets through: the bottleneck is the most delivered by a round with a
// queue behind it, over the last ten such rounds; the link runs a little under that, and every few rounds a
// little over to find out whether there is more. -xlinka
internal sealed class SteamPace
{
    public const int Floor = 256 * 1024;
    public const int Ceiling = 96 * 1024 * 1024;
    public const double StartRate = 1024 * 1024;

    private const double StartGain = 1.3;
    private const double CruiseGain = 0.92;
    private const double ProbeGain = 1.12;
    private const double DrainGain = 0.7;
    private const int ProbeEvery = 8;
    // A link idle this many rounds may be another link by now (another app took the uplink, or left it).
    private const int IdleRoundsBeforeRestart = 20;
    private const double MinRoundMs = 100;
    private const double MaxRoundMs = 400;

    private readonly double[] _rounds = new double[10];
    private int _roundCount;
    private double _bottleneck = StartRate;
    private double _roundStart = -1;
    private long _roundAcked;
    private long _roundUnreliable;
    private int _updates;
    private int _busyUpdates;
    private bool _startup = true;
    private double _startupBest;
    private int _flatRounds;
    private int _sinceProbe;
    private int _idleRounds;
    private double _gain = StartGain;
    private double _minPing = double.MaxValue;
    private double _minPingAt;
    // Rounds since the ping last rose over its floor.
    private const int CongestedRounds = 2;
    private int _sinceQueueing = int.MaxValue;
    private const int QueuedRoundsBeforeNewFloor = 3;
    private int _queuedRounds;

    public double Rate { get; private set; } = StartRate * StartGain;
    public double Bottleneck => _bottleneck;
    public bool Starting => _startup;

    // The live channel may take all of it; the bulk channel gets what the live one leaves, at least a floor so a
    // transfer always moves. While the link shows a queue, and for a few rounds after, the bulk channel keeps only a
    // small share and that comes out of the live channel's rate instead of on top of it: laid on top, a busy live
    // channel and a transfer ran a thin link a floor's worth over what it carries, and what queued at its bottleneck
    // queued in front of the world's changes. A link that never queues keeps the bulk channel's whole rate. -xlinka
    public int LiveRate(bool bulkWaiting) => (int)(Congested && bulkWaiting ? Rate - BulkShare : Rate);

    // A ping rise alone does not mean live traffic needs the whole link. Only reserve it while live
    // data is waiting; otherwise downloads can use the spare capacity at the already reduced rate. -xlinka
    public int BulkRate(double liveOutBytesPerSecond, bool liveWaiting = false)
        => (int)(Congested && liveWaiting ? BulkShare
            : System.Math.Max(Congested ? BulkShare : Floor, Rate - System.Math.Max(0, liveOutBytesPerSecond)));

    public bool Congested => _sinceQueueing <= CongestedRounds;

    private double BulkShare => System.Math.Min(Floor, Rate / 8);

    // So far, both channels: acked, reliable bytes the far end acknowledged; unreliableOut, unreliable bytes that
    // left for the wire. queued: bytes still waiting for the wire.
    public void Update(double nowMs, long acked, long unreliableOut, long queued, int pingMs)
    {
        if (pingMs > 0 && (pingMs <= _minPing || nowMs - _minPingAt > 10000))
        {
            _minPing = pingMs;
            _minPingAt = nowMs;
        }
        if (_roundStart < 0)
        {
            _roundStart = nowMs;
            _roundAcked = acked;
            _roundUnreliable = unreliableOut;
            return;
        }
        _updates++;
        if (queued > System.Math.Max(8 * 1024, Rate * 0.005))
            _busyUpdates++;

        double roundMs = _minPing < double.MaxValue ? System.Math.Clamp(_minPing * 1.5, MinRoundMs, MaxRoundMs) : MinRoundMs;
        double elapsed = nowMs - _roundStart;
        if (elapsed < roundMs)
            return;

        // What got through: the reliable bytes acknowledged and the unreliable ones that left, which nobody
        // acknowledges; the rate pays for both. Counting the reliable alone read a live channel carrying voice and
        // poses as that much slower than it was, every busy round, and the rate walked down to its floor with the
        // world's changes seconds deep behind it. Unreliable bytes a full queue drops are counted all the same, and
        // what that overstates shows up as the ping rising. -xlinka
        double delivered = (System.Math.Max(0, acked - _roundAcked) + System.Math.Max(0, unreliableOut - _roundUnreliable)) * 1000.0 / elapsed;
        bool busy = _busyUpdates * 2 >= _updates;
        _roundStart = nowMs;
        _roundAcked = acked;
        _roundUnreliable = unreliableOut;
        _updates = 0;
        _busyUpdates = 0;

        // A round with nothing waiting only delivered what it was given, which says nothing of what the link takes.
        if (busy || delivered > _bottleneck)
        {
            _rounds[_roundCount++ % _rounds.Length] = delivered;
            double best = 0;
            int kept = System.Math.Min(_roundCount, _rounds.Length);
            for (int i = 0; i < kept; i++)
                best = System.Math.Max(best, _rounds[i]);
            _bottleneck = System.Math.Max(Floor, best);
        }

        if (!busy)
        {
            _idleRounds++;
        }
        else
        {
            if (_idleRounds >= IdleRoundsBeforeRestart && !_startup)
            {
                _startup = true;
                _flatRounds = 0;
                _startupBest = _bottleneck;
            }
            _idleRounds = 0;
        }

        // The ping rising over its floor is the bottleneck's queue filling: let it drain before it drops anything.
        bool queueing = _minPing < double.MaxValue && pingMs > _minPing + System.Math.Max(10, _minPing * 0.2);
        // A queue of ours drains when we send less. A ping still up after a few rounds of that was never our queue
        // (a busy machine at either end, a new route, someone else on the line), and taking it for one drained the
        // rate a third every round until a 1.5 MB/s link ran at 300 KB/s with the world's changes seconds deep. The
        // ping it settled at is the floor now. -xlinka
        _queuedRounds = queueing ? _queuedRounds + 1 : 0;
        if (_queuedRounds > QueuedRoundsBeforeNewFloor)
        {
            _minPing = pingMs;
            _minPingAt = nowMs;
            _queuedRounds = 0;
            queueing = false;
        }
        _sinceQueueing = queueing ? 0 : _sinceQueueing == int.MaxValue ? int.MaxValue : _sinceQueueing + 1;
        if (_startup)
        {
            if (queueing)
            {
                _startup = false;
            }
            else if (busy)
            {
                if (_bottleneck >= _startupBest * 1.15)
                {
                    _startupBest = _bottleneck;
                    _flatRounds = 0;
                }
                else if (++_flatRounds >= 2)
                {
                    _startup = false;
                }
            }
            _gain = _startup ? StartGain : DrainGain;
            _sinceProbe = 0;
        }
        else if (queueing)
        {
            _gain = DrainGain;
            _sinceProbe = 0;
        }
        else if (++_sinceProbe >= ProbeEvery && busy)
        {
            _gain = ProbeGain;
            _sinceProbe = -1;
        }
        else
        {
            _gain = _sinceProbe == 0 ? DrainGain : CruiseGain;
        }

        Rate = System.Math.Clamp(_bottleneck * _gain, Floor, Ceiling);
    }
}
#endif
