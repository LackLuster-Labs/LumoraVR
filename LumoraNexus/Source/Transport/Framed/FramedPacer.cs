// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Diagnostics;
using System.Threading;

namespace Lumora.Nexus.Transport.Framed;

// How much a TCP link may have written that the other side has not yet confirmed. TCP itself fills every queue on
// the way, the kernel's and the router's, before it slows down, and a fresh pose then waits behind all of it:
// on a 50 Mbit/s path with 80 ms round trips a foreground message sat 225 ms at the 95th percentile under bulk.
// So the receiver confirms what it has read every 32 KB, and the sender keeps what is unconfirmed at a quarter
// more than what the path delivered in its shortest round trip (rate from the confirmations, round trip from the
// pings): enough to keep the path full and still grow, too little for a standing queue. A round trip measured
// under load would feed its own queue back in, so the shortest one only ever rises a quarter per ten seconds,
// which still follows a route that really got longer. -xlinka
internal sealed class FramedPacer
{
    public const int AckEvery = 32 * 1024;
    private const long MinWindow = 128 * 1024;
    private const long StartWindow = 256 * 1024;
    private const long MaxWindow = 32L * 1024 * 1024;
    private const double Gain = 1.25;
    private const int RateSamples = 8;
    private const double MinSampleMs = 50;
    private static readonly long RttLifetimeTicks = Stopwatch.Frequency * 10;

    private readonly object _gate = new();
    private readonly double[] _rates = new double[RateSamples];
    private int _nextRate;
    private double _maxRate;
    private long _acked;
    private long _sampleStartTicks;
    private long _sampleStartAcked;
    private double _minRttMs = -1;
    private long _minRttTicks;
    private long _window = StartWindow;

    public long Acked => Interlocked.Read(ref _acked);

    public long Window => Interlocked.Read(ref _window);

    // What may still be written, given what has been written so far.
    public long Allowance(long written) => Window - (written - Acked);

    public void OnRtt(double ms)
    {
        if (ms <= 0)
            return;
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_minRttMs < 0 || ms <= _minRttMs)
            {
                _minRttMs = ms;
                _minRttTicks = now;
            }
            else if (now - _minRttTicks > RttLifetimeTicks)
            {
                _minRttMs = Math.Min(ms, _minRttMs * 1.25);
                _minRttTicks = now;
            }
            Recompute();
        }
    }

    // False for a confirmation that says nothing new.
    public bool OnAck(long total)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (total <= _acked)
                return false;
            Interlocked.Exchange(ref _acked, total);
            if (_sampleStartTicks == 0)
            {
                _sampleStartTicks = now;
                _sampleStartAcked = total;
                return true;
            }
            double elapsedMs = (now - _sampleStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= Math.Max(MinSampleMs, _minRttMs))
            {
                _rates[_nextRate] = (total - _sampleStartAcked) * 1000.0 / elapsedMs;
                _nextRate = (_nextRate + 1) % RateSamples;
                _maxRate = 0;
                foreach (double rate in _rates)
                    _maxRate = Math.Max(_maxRate, rate);
                _sampleStartTicks = now;
                _sampleStartAcked = total;
                Recompute();
            }
            return true;
        }
    }

    private void Recompute()
    {
        if (_maxRate <= 0 || _minRttMs < 0)
            return;
        long window = (long)(_maxRate * Math.Max(_minRttMs, 1) / 1000.0 * Gain);
        Interlocked.Exchange(ref _window, Math.Clamp(window, MinWindow, MaxWindow));
    }
}
