// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lumora.Core.Assets;

// Where a texture load spends its time, stage by stage: asked for, bytes, a delivery turn, decoded, handed
// to the renderer, encoded, queued for the main thread, built. Off unless something turns it on, and then a
// stamp is a timestamp and a queue add. The frame log (--Lumora-FrameLog) turns it on and writes the stamps
// beside its frame rows. -xlinka
public static class TextureLoadTrace
{
    public readonly record struct Stamp(int Id, string Stage, long Ticks, int Thread, string? Detail);

    public static volatile bool Enabled;

    private static readonly ConcurrentQueue<Stamp> _stamps = new();
    private static readonly ConditionalWeakTable<object, StrongBox<int>> _ids = new();
    private static int _nextId;

    private static int IdOf(object owner) =>
        _ids.GetValue(owner, static _ => new StrongBox<int>(Interlocked.Increment(ref _nextId))).Value;

    public static void Mark(object? owner, string stage, string? detail = null)
    {
        if (!Enabled || owner == null)
            return;
        _stamps.Enqueue(new Stamp(IdOf(owner), stage, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, detail));
    }

    public static List<Stamp> Drain()
    {
        var drained = new List<Stamp>(_stamps.Count);
        while (_stamps.TryDequeue(out var stamp))
            drained.Add(stamp);
        return drained;
    }
}
