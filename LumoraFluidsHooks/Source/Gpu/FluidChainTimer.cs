// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Godot;
using GodotEngine = Godot.Engine;

namespace Lumora.Fluids.Hooks;

// GPU time of one world's whole fluid frame: two timestamps round the solver, the surfaces and the residue.
//
// The device hands timestamps back when it next starts that frame slot, frame_queue_size frames later, and only that
// slot's are readable then. Anything that flushes the device in the middle of a frame (a synchronous read, a full
// staging buffer, a swapchain resize) starts the slot again on the spot: what was marked so far is resolved then and
// overwritten before anyone reads it, and a pair marked across the flush comes back in two halves whose gap holds the
// stall. So a reading counts only when both marks come back in one batch; anything else is dropped and counted, and
// until a fresh reading lands the time is NaN, never a number that only looks like one (a timer that never resolved
// used to read 0 ms, and the budget took that as free). -xlinka
public sealed class FluidChainTimer
{
    private const int Ring = 8;

    private static int _nextId;

    private readonly string _prefix;
    private readonly string[] _begin = new string[Ring];
    private readonly string[] _end = new string[Ring];
    private readonly double[] _seenBegin = new double[Ring];
    private readonly double[] _seenEnd = new double[Ring];
    private int _ring;
    private double _lastBegin = -1;
    private double _lastLoneEnd = -1;

    public FluidChainTimer(RenderingDevice device)
    {
        Device = device;
        _prefix = "fluidchain" + _nextId++ + "|";
        for (int i = 0; i < Ring; i++)
        {
            _begin[i] = _prefix + i + "|b";
            _end[i] = _prefix + i + "|e";
        }
    }

    public RenderingDevice Device { get; }

    // The last reading, NaN before the first.
    public double LastGpuMs { get; private set; } = double.NaN;

    public long Readings { get; private set; }

    // Ends that came back without their begin: a flush split the pair.
    public long Splits { get; private set; }

    // Engine frames drawn when the last reading landed.
    public ulong LastReadingFrame { get; private set; }

    // The last reading while it is at most maxFrames drawn frames old, else NaN.
    public double FreshMs(int maxFrames)
        => Readings > 0 && (ulong)GodotEngine.GetFramesDrawn() - LastReadingFrame <= (ulong)maxFrames ? LastGpuMs : double.NaN;

    public void Begin()
    {
        _ring = (_ring + 1) % Ring;
        Device.CaptureTimestamp(_begin[_ring]);
    }

    public void End() => Device.CaptureTimestamp(_end[_ring]);

    // Reads this slot's batch. Called each frame before the next Begin.
    public void Collect()
    {
        uint count = Device.GetCapturedTimestampsCount();
        if (count == 0)
            return;
        Array.Fill(_seenBegin, -1);
        Array.Fill(_seenEnd, -1);
        bool any = false;
        int at = _prefix.Length;
        for (uint i = 0; i < count; i++)
        {
            string name = Device.GetCapturedTimestampName(i);
            if (name.Length != at + 3 || !name.StartsWith(_prefix, StringComparison.Ordinal))
                continue;
            int slot = name[at] - '0';
            if ((uint)slot >= Ring)
                continue;
            double t = Device.GetCapturedTimestampGpuTime(i) / 1e6;
            if (name[at + 2] == 'b')
                _seenBegin[slot] = t;
            else
                _seenEnd[slot] = t;
            any = true;
        }
        if (!any)
            return;
        double begin = -1, end = -1;
        for (int r = 0; r < Ring; r++)
        {
            if (_seenEnd[r] < 0)
                continue;
            if (_seenBegin[r] < 0)
            {
                if (_seenEnd[r] != _lastLoneEnd)
                {
                    _lastLoneEnd = _seenEnd[r];
                    Splits++;
                }
                continue;
            }
            if (_seenBegin[r] > begin && _seenEnd[r] >= _seenBegin[r])
            {
                begin = _seenBegin[r];
                end = _seenEnd[r];
            }
        }
        // The same batch can be read twice before its slot comes round again; it counts once.
        if (begin < 0 || begin == _lastBegin)
            return;
        _lastBegin = begin;
        LastGpuMs = end - begin;
        Readings++;
        LastReadingFrame = (ulong)GodotEngine.GetFramesDrawn();
    }
}
