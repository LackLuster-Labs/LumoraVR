// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Godot;

namespace Lumora.Fluids.Hooks;

// GPU time of the fluid chain. In the game, two timestamps bracket the whole chain and come back frame_queue_size
// frames later; their names come from a small ring made once, so marking costs no allocation. PerKernel (tests,
// and the inspector's Measure GPU) adds a mark after every dependent kernel; each sits between compute
// lists, the only place the device accepts one. Godot's own profiler shares the pool, so marks are found by name.
// A reading counts only when a slot's begin and end come back in one batch (FluidChainTimer's rule): a mid-frame
// flush of the device (a synchronous read, a full staging buffer, a resize) resolves marks early and splits pairs,
// and a begin and end from different frames read as a stall. Until a reading lands the time is NaN. -xlinka
public sealed class FluidGpuTimers
{
    private const int Ring = 4;

    private static int _nextId;

    private readonly string _prefix;
    private readonly string[] _begin = new string[Ring];
    private readonly string[] _end = new string[Ring];
    private int _ring;
    private readonly double[] _seenBegin = new double[Ring];
    private readonly double[] _seenEnd = new double[Ring];
    private double _lastBegin = -1;
    private double _lastLoneEnd = -1;

    public FluidGpuTimers(RenderingDevice device)
    {
        Device = device;
        _prefix = "fluid" + _nextId++ + "|";
        for (int i = 0; i < Ring; i++)
        {
            _begin[i] = _prefix + i + "|begin";
            _end[i] = _prefix + i + "|end";
        }
    }

    public RenderingDevice Device { get; }

    public bool PerKernel { get; set; }

    // The last reading, NaN before the first.
    public double LastGpuMs { get; private set; } = double.NaN;

    public long Readings { get; private set; }

    // Ends that came back without their begin: a flush split the pair.
    public long Splits { get; private set; }

    // Per kernel GPU ms of the last frame that came back, in recording order (tests only).
    public IReadOnlyList<(string Kernel, double Ms)> LastKernels => _lastKernels;

    private readonly List<(string Kernel, double Ms)> _lastKernels = new();

    public void Begin()
    {
        _ring = (_ring + 1) % Ring;
        Device.CaptureTimestamp(_begin[_ring]);
    }

    public void Mark(string kernel)
    {
        if (!PerKernel)
            return;
        Device.CaptureTimestamp(_prefix + _ring + "|k|" + kernel);
    }

    public void End()
    {
        Device.CaptureTimestamp(_end[_ring]);
    }

    // Reads whatever of ours the device has handed back since the last call.
    public void Collect()
    {
        uint count = Device.GetCapturedTimestampsCount();
        if (count == 0)
            return;
        Array.Fill(_seenBegin, -1);
        Array.Fill(_seenEnd, -1);
        for (uint i = 0; i < count; i++)
        {
            string name = Device.GetCapturedTimestampName(i);
            if (!name.StartsWith(_prefix, StringComparison.Ordinal) || name.Length <= _prefix.Length + 1)
                continue;
            int slot = name[_prefix.Length] - '0';
            if ((uint)slot >= Ring || name[_prefix.Length + 1] != '|')
                continue;
            double t = Device.GetCapturedTimestampGpuTime(i) / 1e6;
            if (name.EndsWith("|begin", StringComparison.Ordinal))
                _seenBegin[slot] = t;
            else if (name.EndsWith("|end", StringComparison.Ordinal))
                _seenEnd[slot] = t;
        }
        int best = -1;
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
            if (_seenEnd[r] >= _seenBegin[r] && (best < 0 || _seenBegin[r] > _seenBegin[best]))
                best = r;
        }
        // the same batch can be read twice before its slot comes round again; it counts once
        if (best < 0 || _seenBegin[best] == _lastBegin)
            return;
        _lastBegin = _seenBegin[best];
        LastGpuMs = _seenEnd[best] - _seenBegin[best];
        Readings++;
        if (!PerKernel)
            return;
        _lastKernels.Clear();
        double previous = _seenBegin[best];
        string kernelPrefix = _prefix + best + "|k|";
        for (uint i = 0; i < count; i++)
        {
            string name = Device.GetCapturedTimestampName(i);
            if (!name.StartsWith(kernelPrefix, StringComparison.Ordinal))
                continue;
            double t = Device.GetCapturedTimestampGpuTime(i) / 1e6;
            _lastKernels.Add((name[kernelPrefix.Length..], t - previous));
            previous = t;
        }
    }
}
