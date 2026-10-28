// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Nexus.Transport.LNL;

// How fast one LNL peer's link carries data, found from what actually got through. LNL sends whatever it is
// handed, as fast as its windows allow: 64 packets on every reliable channel, unreliable without any limit. On
// an 80 ms, 50 Mbit/s path with 50 ms of queue that dropped millions of datagrams at the bottleneck, and a
// reliable message waited behind an unreliable flood for seconds.
//
// Every acknowledged send is a sample: what got through between its leaving and its acknowledgement, over that
// time. What got through is reliable bytes the far end acknowledged and unreliable bytes that left, which nobody
// acknowledges; both count, so a link carrying voice and poses does not read slower than it is. The rate is the
// best sample of the last eight busy rounds; a round that was given little says nothing of the link and only
// counts if it beat the rate.
//
// Three things bring the rate down. The fastest acknowledgement of a round well over the shortest round trip
// seen is a queue standing at the bottleneck. LNL putting noticeably more on the wire than it was handed is
// resending what a queue that overflowed dropped. Small messages coming back a resend late is a bottleneck that
// drops without queueing at all, as a relay's rate cap does. Each way the rate drops and the link gets time to
// drain. A message bigger than one packet reads the queue too, once its own time on the wire is taken off. LNL's
// own loss count is no use here: it counts every packet an acknowledgement has not reached yet.
//
// Data held to the rate can only ever measure the rate, so one round in eight sends a quarter over, to see
// whether more gets through, and the next a quarter under, to drain what that left queued. -xlinka
internal sealed class LNLPace
{
    public struct Stamp
    {
        public double SentAt;
        public long Delivered;
        public double DeliveredAt;
        public double FirstSentAt;
    }

    private const double StartRate = 1024 * 1024;
    private const double Floor = 64 * 1024;
    private const double Ceiling = 2048.0 * 1024 * 1024;
    private const long MinWindow = 64 * 1024;
    private const long MaxWindow = 64L * 1024 * 1024;
    private const double StartupGain = 2.0;
    private const double Backoff = 0.85;
    private const double ReliableFloorShare = 0.15;
    private const int Samples = 8;
    private static readonly double[] Cycle = { 1.25, 0.75, 1, 1, 1, 1, 1, 1 };
    private const double MinRoundMs = 30;
    private const double MaxRoundMs = 500;
    // The shortest round trip is the least of this stretch and the last, so a longer route is followed.
    private const double RttEpochMs = 10000;
    // The far end acknowledges on its next update, up to this long after a packet lands (LNLLanes.Configure).
    // Sends that follow acknowledgements land just after one and wait the whole of it.
    private const double AckDelayMs = 15;
    // A standing queue past the shortest round trip, the acknowledgement wait and this share of it (at least the
    // floor, at most the cap) is too long. The floor is for a near link, where a busy machine is slower to answer
    // than the round trip itself.
    private const double QueueShare = 0.25;
    private const double QueueFloorMs = 10;
    private const double QueueCapMs = 25;
    // LNL's headers come to about this much a packet, acknowledgements included. Wire bytes past what a round
    // handed over, its headers and this share and slack, are resends.
    private const int PacketOverhead = 12;
    private const int FragmentBytes = LNLLanes.PacketBytes - 10;
    private const double ResendShare = 0.03;
    private const long ResendSlack = 4096;
    // LNL resends a packet this long plus about twice the round trip after it left (its ResendFixedDelay).
    private const double ResendDelayMs = 25;

    // What LNL can have out on one channel; a bigger message crosses over several round trips.
    private const long ChannelWindowBytes = 64L * LNLLanes.PacketBytes;

    // The shortest round trip is only read off messages that fit one packet: a bigger one's includes its own time
    // on the wire.
    private const int RttSampleBytes = 1200;

    private readonly double[] _samples = new double[Samples];
    private int _sampleCount;
    private double _rate = StartRate;
    private bool _startup = true;
    private double _startupBest;
    private int _flatRounds;
    private int _drainRounds;
    private int _resendRounds;

    private double _minRtt = -1;
    private double _epochMin = double.MaxValue;
    private double _lastEpochMin = double.MaxValue;
    private double _epochStart;
    private double _roundMinRtt = double.MaxValue;

    private double _roundStart = -1;
    private long _roundUnreliable;
    private long _roundHanded;
    private long _roundPackets;
    private long _roundWire;
    private long _roundReceived;
    private double _roundBest;
    private bool _roundBusy;
    private int _roundSmall;
    private int _roundLate;
    private double _unreliableRate;
    private int _cycle;

    private long _delivered;
    private double _deliveredAt;
    private double _firstSentAt;
    private double _lumpAt = double.MinValue;
    private long _roundMaxMessage;
    private long _lastMaxMessage;
    private int _inFlight;
    private long _unreliable;
    private long _handed;
    private long _packets;

    public LNLPace()
    {
        // The start rate holds until busy rounds have measured the link, or startup would open on one lone sample.
        _samples[0] = StartRate;
        _sampleCount = 1;
    }

    public double MinRttMs => _minRtt;

    private double Gain => _startup ? StartupGain : Cycle[_cycle];

    // How long a send waits for its acknowledgement when nothing is queued.
    private double FeedbackMs => (_minRtt > 0 ? _minRtt : 100) + AckDelayMs;

    // Bytes a second the outbox may send, every kind together.
    public double SendRate => _rate * Gain;

    // Reliable bytes the outbox may have handed LNL and not had acknowledged. A message is only acknowledged
    // once all of it is through, so the window holds one message more than the link does: otherwise the link
    // idles while the last of each message comes back, and on a thin link with big messages the rate reads
    // only what the window lets through.
    public long Window
    {
        get
        {
            double reliableRate = Math.Max(_rate * ReliableFloorShare, _rate - _unreliableRate);
            long message = Math.Max(_roundMaxMessage, _lastMaxMessage);
            return Math.Clamp((long)(Gain * (reliableRate * FeedbackMs / 1000.0 + message)), MinWindow, MaxWindow);
        }
    }

    public void SeedRtt(double ms, double nowMs)
    {
        if (ms > 0 && _minRtt < 0)
        {
            _minRtt = ms;
            _epochMin = ms;
            _epochStart = nowMs;
        }
    }

    public void OnUnreliableSent(int bytes, double nowMs)
    {
        _unreliable += bytes;
        _packets++;
        _delivered += bytes;
        _deliveredAt = nowMs;
    }

    public Stamp OnSent(int bytes, double nowMs)
    {
        if (_inFlight == 0)
        {
            _firstSentAt = nowMs;
            _deliveredAt = nowMs;
        }
        _inFlight++;
        _handed += bytes;
        _roundMaxMessage = Math.Max(_roundMaxMessage, Math.Min(bytes, ChannelWindowBytes));
        _packets += 1 + (bytes - 1) / FragmentBytes;
        return new Stamp { SentAt = nowMs, Delivered = _delivered, DeliveredAt = _deliveredAt, FirstSentAt = _firstSentAt };
    }

    // clean: the message had its channel to itself, so its round trip is the link's and not a wait behind others.
    public void OnDelivered(in Stamp stamp, int bytes, bool clean, double nowMs)
    {
        _delivered += bytes;
        _deliveredAt = nowMs;
        _firstSentAt = stamp.SentAt;
        if (_inFlight > 0)
            _inFlight--;

        double rtt = nowMs - stamp.SentAt;
        // A message that crossed several round trips, or waited on a resend, lands all at once long after it
        // left. Counted into a send that happened to be out at the time, it would read as a burst of speed.
        if (bytes > ChannelWindowBytes || rtt > FeedbackMs * 2 + bytes * 1000.0 / _rate)
            _lumpAt = nowMs;
        if (clean && rtt > 0)
        {
            if (bytes <= RttSampleBytes)
            {
                OnRtt(rtt, nowMs);
                _roundSmall++;
                if (_minRtt > 0 && rtt > _minRtt * 2 + ResendDelayMs)
                    _roundLate++;
            }
            else
                _roundMinRtt = Math.Min(_roundMinRtt, Math.Max(0, rtt - bytes * 1000.0 / _rate));
        }

        // Over the longer of how long the sends took to leave and their acknowledgements took to come back, so a
        // clump of acknowledgements arriving together does not read as a burst of speed.
        double interval = Math.Max(stamp.SentAt - stamp.FirstSentAt, nowMs - stamp.DeliveredAt);
        if (interval <= 0 || (_minRtt > 0 && interval < _minRtt) || _lumpAt >= stamp.DeliveredAt)
            return;
        double sample = (_delivered - stamp.Delivered) * 1000.0 / interval;
        if (sample > _roundBest)
            _roundBest = sample;
    }

    private void OnRtt(double rttMs, double nowMs)
    {
        if (rttMs < _roundMinRtt)
            _roundMinRtt = rttMs;
        if (nowMs - _epochStart >= RttEpochMs)
        {
            if (_epochMin < double.MaxValue)
                _lastEpochMin = _epochMin;
            _epochMin = double.MaxValue;
            _epochStart = nowMs;
        }
        _epochMin = Math.Min(_epochMin, rttMs);
        _minRtt = Math.Min(_epochMin, _lastEpochMin);
    }

    // Something waited on the budget this round.
    public void MarkBusy() => _roundBusy = true;

    // wireSent and received: LNL's counts for this peer so far. heldBack: bytes handed over that LNL holds unsent
    // because their channel's window is full.
    public void Tick(double nowMs, long wireSent, long received, long heldBack)
    {
        if (_roundStart < 0)
        {
            StartRound(nowMs, wireSent, received, heldBack);
            return;
        }
        double elapsed = nowMs - _roundStart;
        if (elapsed < Math.Clamp(FeedbackMs * 1.25, MinRoundMs, MaxRoundMs))
            return;

        _unreliableRate = (_unreliable - _roundUnreliable) * 1000.0 / elapsed;
        // A send's sample spans the round after it, so a probe made of unreliable data alone shows in what the
        // round itself sent.
        double sample = Math.Max(_roundBest, _unreliableRate);
        bool busy = _roundBusy;
        long handed = _handed + _unreliable - heldBack - _roundHanded;
        long expected = handed + PacketOverhead * (_packets - _roundPackets + received - _roundReceived);
        long wire = wireSent - _roundWire;
        // Only a round sent at the rate itself says anything about a standing queue: the probe round makes one
        // on purpose and the round after it is draining it.
        bool steady = _startup || Cycle[_cycle] == 1;
        bool queueing = steady && _minRtt > 0 && _roundMinRtt < double.MaxValue && _roundMinRtt > _minRtt + Math.Clamp(_minRtt * QueueShare, QueueFloorMs, QueueCapMs) + AckDelayMs;
        // Resends two rounds running: one round of them is as often a stall on either machine, LNL resending
        // whatever waited past its timer, as a queue that overflowed.
        _resendRounds = busy && handed > 0 && wire - expected > expected * ResendShare + ResendSlack ? _resendRounds + 1 : 0;
        // One late small message among many is the odd loss any path has; a few, or a good share, is a cap.
        bool capped = _roundLate >= 2 || (_roundLate > 0 && _roundLate * 4 >= _roundSmall);
        bool overflowed = _resendRounds >= 2 || capped;
        StartRound(nowMs, wireSent, received, heldBack);
        if (!_startup)
            _cycle = (_cycle + 1) % Cycle.Length;

        if (_drainRounds > 0)
        {
            _drainRounds--;
        }
        else if (queueing || overflowed)
        {
            // Come down, forget the rounds that built the queue, and give it time to drain before judging again:
            // LNL's resends of one overflow trickle in over a couple of rounds.
            _rate = Math.Max(Floor, _rate * Backoff);
            Array.Clear(_samples);
            _samples[0] = _rate;
            _sampleCount = 1;
            _startup = false;
            _drainRounds = 2;
            _resendRounds = 0;
            return;
        }
        if (sample <= 0 || (!busy && sample <= _rate))
            return;

        _samples[_sampleCount++ % Samples] = sample;
        double best = 0;
        for (int i = 0; i < Math.Min(_sampleCount, Samples); i++)
            best = Math.Max(best, _samples[i]);
        _rate = Math.Clamp(best, Floor, Ceiling);

        if (_startup && busy)
        {
            if (_rate >= _startupBest * 1.25)
            {
                _startupBest = _rate;
                _flatRounds = 0;
            }
            else if (++_flatRounds >= 3)
            {
                _startup = false;
            }
        }
    }

    private void StartRound(double nowMs, long wireSent, long received, long heldBack)
    {
        _roundStart = nowMs;
        _roundUnreliable = _unreliable;
        _roundHanded = _handed + _unreliable - heldBack;
        _roundPackets = _packets;
        _roundWire = wireSent;
        _roundReceived = received;
        _roundBest = 0;
        _roundBusy = false;
        _roundSmall = 0;
        _roundLate = 0;
        _lastMaxMessage = _roundMaxMessage;
        _roundMaxMessage = 0;
        _roundMinRtt = double.MaxValue;
    }
}
