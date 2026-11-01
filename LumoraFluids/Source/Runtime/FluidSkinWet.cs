// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;

namespace Lumora.Fluids;

// A world's inbox for the skin collision's wet patches. The GPU side posts a decoded read-back from the render thread
// (the device hands read-backs back inside its frame draw); the people link takes them on the world thread in its
// update and hands the wearer's own to the baker. Nothing here touches the scene. -xlinka
public sealed class FluidSkinWet
{
    // samples kept at most between two takes; a stalled world never grows without bound
    public const int MaxQueued = 4096;

    private static readonly ConditionalWeakTable<World, FluidSkinWet> ByWorld = new();

    private readonly ConcurrentQueue<FluidWetSample> _queue = new();

    public long Posted { get; private set; }

    public long Dropped { get; private set; }

    public static FluidSkinWet For(World world) => ByWorld.GetValue(world, _ => new FluidSkinWet());

    public static FluidSkinWet? Find(World? world) => world != null && ByWorld.TryGetValue(world, out var wet) ? wet : null;

    public void Post(IReadOnlyList<FluidWetSample> samples)
    {
        foreach (var s in samples)
        {
            if (_queue.Count >= MaxQueued)
            {
                Dropped++;
                continue;
            }
            _queue.Enqueue(s);
            Posted++;
        }
    }

    public int TakeAll(List<FluidWetSample> into)
    {
        int n = 0;
        while (_queue.TryDequeue(out var s))
        {
            into.Add(s);
            n++;
        }
        return n;
    }
}
