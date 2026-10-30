// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;

namespace Lumora.Fluids;

// Summaries waiting for the world thread. The read-back callback lands inside Godot's frame draw, after every
// viewport and outside any world update, so it only copies the bytes in here and FluidSpaceDriver takes them on
// its next update. Buffers are pooled: past warm-up a summary costs one copy on our side. At most three wait; a
// world that stops updating drops the oldest, which is stale by then anyway. -xlinka
public sealed class FluidSummaryQueue
{
    private const int MaxWaiting = 3;

    private readonly object _lock = new();
    private readonly Queue<FluidSummaryView> _waiting = new();
    private readonly Stack<byte[]> _free = new();

    public int Dropped { get; private set; }

    public int Count
    {
        get
        {
            lock (_lock)
                return _waiting.Count;
        }
    }

    // The callback side: copies, and never keeps the array the binding handed it.
    public void Push(ReadOnlySpan<byte> data)
    {
        int length = Math.Min(data.Length, FluidLayout.SummaryBytes);
        lock (_lock)
        {
            if (_waiting.Count >= MaxWaiting)
            {
                var old = _waiting.Dequeue();
                if (old.Buffer != null)
                    _free.Push(old.Buffer);
                Dropped++;
            }
            var buffer = _free.Count > 0 ? _free.Pop() : new byte[FluidLayout.SummaryBytes];
            data[..length].CopyTo(buffer);
            _waiting.Enqueue(new FluidSummaryView(buffer, length));
        }
    }

    public bool TryTake(out FluidSummaryView view)
    {
        lock (_lock)
            return _waiting.TryDequeue(out view);
    }

    public void Return(FluidSummaryView view)
    {
        if (view.Buffer is not { Length: FluidLayout.SummaryBytes } buffer)
            return;
        lock (_lock)
            _free.Push(buffer);
    }

    public void Clear()
    {
        lock (_lock)
        {
            while (_waiting.TryDequeue(out var view))
            {
                if (view.Buffer != null)
                    _free.Push(view.Buffer);
            }
        }
    }
}
