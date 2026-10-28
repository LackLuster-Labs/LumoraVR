// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Godot;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Godot.Hooks;

// Engine work that has to run on the main thread, finished a few milliseconds of it a frame: texture,
// mesh, skin and cubemap builds handed back from the threads that prepared them. Handed back with a plain
// deferred call they all ran in the one flush that followed, and an avatar or an import arriving is a
// hundred of them landing together: the frame they landed in lasted seconds, outside anything the world's
// own frame report could see. At least one runs a frame, so one big build still gets through; any single
// build slow enough to hold a frame on its own says so by name.
//
// Time alone does not hold it: handing a texture over returns at once and the copy to the GPU happens
// later in the frame, where no timer here sees it. So work that carries data says how much, and a frame
// takes no more than ByteBudget of it. -xlinka
//
// Pictures have a lane of their own, and each lane a floor the shared load allowance cannot take away.
// It was one queue drained from whatever allowance the worlds left, and a world settling an import spends
// all of it for minutes: one build ran a frame, the import's seven thousand mesh builds went first, and its
// textures reached the screen two minutes later and all together. A picture costs the bytes it copies, so
// its lane runs first and always gets PictureFloorBytes of them; builds always get BuildFloorMs. Past the
// floors both share the allowance as before. -xlinka
public static class MainThreadWork
{
    private const double BudgetMs = 4.0;
    private const long ByteBudget = 32L * 1024 * 1024;
    private const double SlowMs = 100.0;
    private const long PictureFloorBytes = 8L * 1024 * 1024;
    private const double PictureFloorMs = 1.5;
    private const double BuildFloorMs = 1.0;

    private readonly record struct Item(string Label, Action Work, long Bytes);

    private static readonly ConcurrentQueue<Item> _builds = new();
    private static readonly ConcurrentQueue<Item> _pictures = new();
    private static int _attached;

    public static int Pending => _builds.Count + _pictures.Count;

    // The last drain, for the frame readouts: how long it ran, how many it ran, and its slowest item.
    public static double LastDrainMs { get; private set; }
    public static int LastDrainItems { get; private set; }
    public static string LastSlowest { get; private set; } = string.Empty;
    public static double LastSlowestMs { get; private set; }

    // Any thread.
    public static void Post(string label, Action work, long bytes = 0) => Enqueue(_builds, new Item(label, work, bytes));

    // A texture's upload: bytes is what it copies to the GPU. Any thread.
    public static void PostPicture(string label, Action work, long bytes) => Enqueue(_pictures, new Item(label, work, bytes));

    private static void Enqueue(ConcurrentQueue<Item> lane, Item item)
    {
        lane.Enqueue(item);
        if (Volatile.Read(ref _attached) == 0 && Interlocked.CompareExchange(ref _attached, 1, 0) == 0)
            Callable.From(Attach).CallDeferred();
    }

    private static void Attach()
    {
        if (global::Godot.Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame += Drain;
        else
            Volatile.Write(ref _attached, 0);
        Drain();
    }

    private static void Drain()
    {
        LastDrainItems = 0;
        LastSlowestMs = 0;
        if (_pictures.IsEmpty && _builds.IsEmpty)
        {
            LastDrainMs = 0;
            return;
        }
        long started = Stopwatch.GetTimestamp();
        long bytes = 0;

        long pictureBytes = 0;
        bool first = true;
        while (_pictures.TryPeek(out var next))
        {
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bool floor = pictureBytes + next.Bytes <= PictureFloorBytes && ms < PictureFloorMs;
            if (!first && !floor && !Shared(started, bytes + next.Bytes))
                break;
            if (!_pictures.TryDequeue(out var item))
                break;
            first = false;
            pictureBytes += item.Bytes;
            bytes += item.Bytes;
            Run(item, _pictures);
        }

        long buildsStarted = Stopwatch.GetTimestamp();
        first = true;
        while (_builds.TryPeek(out var next))
        {
            bool floor = Stopwatch.GetElapsedTime(buildsStarted).TotalMilliseconds < BuildFloorMs && bytes + next.Bytes <= ByteBudget;
            if (!first && !floor && !Shared(started, bytes + next.Bytes))
                break;
            if (!_builds.TryDequeue(out var item))
                break;
            first = false;
            bytes += item.Bytes;
            Run(item, _builds);
        }
        LastDrainMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Lumora.Core.Engine.ChargeLoad(started);
    }

    // Also bounded by the frame's shared load allowance, of which this takes what the worlds left.
    private static bool Shared(long started, long bytes) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds < BudgetMs && bytes <= ByteBudget
        && !Lumora.Core.Engine.LoadAllowanceSpent(started, Lumora.Core.Engine.LoadShareAll);

    private static void Run(Item item, ConcurrentQueue<Item> lane)
    {
        long itemStarted = Stopwatch.GetTimestamp();
        try
        {
            item.Work();
        }
        catch (Exception ex)
        {
            LumoraLogger.Error($"MainThreadWork: {item.Label} threw: {ex}");
        }
        double ms = Stopwatch.GetElapsedTime(itemStarted).TotalMilliseconds;
        LastDrainItems++;
        if (ms > LastSlowestMs)
        {
            LastSlowestMs = ms;
            LastSlowest = item.Label;
        }
        if (ms >= SlowMs)
            LumoraLogger.Warn($"MainThreadWork: {item.Label} took {ms:0} ms on the main thread ({lane.Count} waiting)");
    }
}
