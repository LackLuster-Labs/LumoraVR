// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Core.Input;

// The first press is handled by the caller. Repeats require that same press to stay down;
// focusing a field with a key already held must never start deleting its contents.
//
// Ticks are laid on the world clock from the moment of the press, not counted down out of frame deltas: a key
// held t seconds has fired once per interval past the delay at any frame rate, the frame the press landed in
// takes nothing off the delay, and a late frame fires every tick that came due while it ran. Only a stall
// longer than CatchUp is not paid back; its older ticks are skipped on the beat. -xlinka
public struct KeyRepeat
{
    public const float DefaultDelay = 0.45f;
    public const float DefaultInterval = 0.05f;
    // A 200 ms hitch costs no characters; a two second load does not empty the field the moment it ends.
    public const double CatchUp = 0.25;
    // A tick due this close after the frame still fires in it, so a tick landing on a frame boundary is not
    // pushed a frame late by the float delay and interval.
    private const double Slack = 1e-4;

    private bool _armed;
    private double _next;

    public void Begin(double now, float delay = DefaultDelay)
    {
        _armed = double.IsFinite(now);
        _next = now + (float.IsFinite(delay) ? MathF.Max(0f, delay) : DefaultDelay);
    }

    public void Reset() => _armed = false;

    public int Advance(bool held, double now, float interval = DefaultInterval)
    {
        if (!held) Reset();
        if (!_armed || !double.IsFinite(now)) return 0;
        double step = float.IsFinite(interval) ? MathF.Max(0.01f, interval) : DefaultInterval;
        double behind = now - CatchUp - _next;
        if (behind > 0.0)
            _next += System.Math.Ceiling(behind / step) * step;
        int count = 0;
        while (_next <= now + Slack)
        {
            count++;
            _next += step;
        }
        return count;
    }
}
