// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Runtime;
using System.Threading;

namespace Lumora.Core;

// Collections paced to the frame. Left alone the runtime lets tens of megabytes pile up before it collects,
// and with a world load's worth of new objects still alive, copying them out was a 30 to 90 ms stop. A young
// collection at the end of any frame that allocated a few megabytes since the last one keeps each to a few
// milliseconds; the total work is the same. Measured on saved world loads: worst pause 78 ms to 24 ms, heap
// less than half. -xlinka
public static class GcPacer
{
    public const long BudgetBytes = 4L * 1024 * 1024;

    // Between arrivals the frame's garbage dies young, and a young collection's cost is mostly its fixed part:
    // finding what the old objects point at, which grows with the heap, not with the garbage. A crowd's
    // steady 18 MB a second at the arrival budget was four collections a second at 2.3 ms each. With nothing
    // arriving the budget is four times as large; anything arriving, or a world still settling, brings back the
    // small one. -xlinka
    public const long SteadyBudgetBytes = 16L * 1024 * 1024;

    private static long _allocatedAt = -1;
    private static int _youngSeen = -1;

    // ONE COLLECTION THAT GIVES MEMORY BACK, ONCE A WORLD HAS ARRIVED.
    //
    // A world's arrival leaves its big buffers dead: decoded textures handed to the renderer, gathered
    // files, maps grown past their size. A full collection frees them, but the runtime keeps the memory
    // they sat in, so after a big world the process held several times its live size, gigabytes for
    // nothing. Once a world has arrived, no world is still loading and no asset has loaded for a moment,
    // one full collection frees everything dead and the runtime returns the space to the system. It runs
    // only after an arrival that allocated a lot, and never while anything is still arriving. -xlinka
    private const long QuietMs = 1500;

    private static int _arrivalPending;
    private static long _arrivalAllocatedAt;
    private static long _quietSince = -1;

    private static long ArrivalWorthSettling => Engine.UseConstrainedResources ? 128L << 20 : 512L << 20;

    public static void WorldArriving()
    {
        if (Interlocked.Exchange(ref _arrivalPending, 1) == 0)
            Volatile.Write(ref _arrivalAllocatedAt, GC.GetTotalAllocatedBytes(false));
        Volatile.Write(ref _quietSince, -1);
    }

    private static void SettleAfterArrival()
    {
        var manager = Engine.Current?.WorldManager;
        if (Assets.Asset.LoadingCount > 0 || (manager != null && manager.AnyWorldLoading()))
        {
            _quietSince = -1;
            return;
        }
        long now = Environment.TickCount64;
        if (_quietSince < 0)
        {
            _quietSince = now;
            return;
        }
        if (now - _quietSince < QuietMs)
            return;

        Volatile.Write(ref _arrivalPending, 0);
        _quietSince = -1;
        if (GC.GetTotalAllocatedBytes(false) - Volatile.Read(ref _arrivalAllocatedAt) < ArrivalWorthSettling)
            return;
        var info = GC.GetGCMemoryInfo();

        // In the background. The compacting pass gave back the most, but it stopped the frame for 400 ms on a
        // crowd's heap, every time an avatar finished importing. A background collection frees the same
        // garbage while frames go on, and the runtime hands the freed space back over the next seconds; the
        // frame pays a few milliseconds of it. -xlinka
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
        long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        Logging.Logger.Log($"GcPacer: a world arrived; a background collection started in {ms} ms ({info.TotalCommittedBytes >> 20} MB held)");
    }

    public static void EndFrame()
    {
        if (Volatile.Read(ref _arrivalPending) != 0)
            SettleAfterArrival();

        long allocated = GC.GetTotalAllocatedBytes(false);
        int young = GC.CollectionCount(0);
        // The first frame, or the runtime collected on its own since the last look: count from here.
        if (_allocatedAt < 0 || young != _youngSeen)
        {
            if (_allocatedAt < 0)
                KeepFullCollectionsInBackground();
            _allocatedAt = allocated;
            _youngSeen = young;
            return;
        }
        long budget = Volatile.Read(ref _arrivalPending) != 0 || Assets.Asset.LoadingCount > 0 ? BudgetBytes : SteadyBudgetBytes;
        if (allocated - _allocatedAt < budget)
            return;
        // Asked of a thread of its own. The count is the whole process's, and once a worker's big arrays (a
        // picture's variants, a file read whole) have used up the large object budget, the runtime makes
        // this young collection a full one, and the blocking call waits for all of it: 30 ms of the main
        // thread, traced right after a 4K picture was saved. A young collection stops every thread the
        // same wherever it is asked from, so only the waiting moves off the frame. Asked without blocking
        // instead, every one of them came out a gen 1, four times the stop. -xlinka
        _allocatedAt = allocated;
        if (NearMemoryLimit())
            return;
        RequestYoungCollection();
    }

    // NO FULL COLLECTION THAT STOPS THE GAME, UNLESS MEMORY IS RUNNING OUT.
    //
    // The runtime picks a blocking full collection over a background one for reasons of its own: a fragmented
    // old generation it wants compacted, among others. On a crowd's heap one of those stopped every thread for
    // 400 to 600 ms. Sustained low latency keeps it to young collections and background full ones, and leaves
    // the blocking kind for a machine short of memory, where the runtime still has it. -xlinka
    private static void KeepFullCollectionsInBackground()
    {
        try
        {
            if (GCSettings.LatencyMode is GCLatencyMode.Interactive or GCLatencyMode.Batch)
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch (Exception)
        {
        }
    }

    // A young collection asked for while the machine is near its memory limit is one the runtime turns into a
    // full blocking one to give memory back, and it stops every thread however it was asked. Past the limit the
    // runtime is left to choose when. Read from the last collection, so only when one is due. -xlinka
    private static bool NearMemoryLimit()
    {
        var info = GC.GetGCMemoryInfo();
        long threshold = info.HighMemoryLoadThresholdBytes;
        return threshold > 0 && info.MemoryLoadBytes >= threshold - threshold / 32;
    }

    private static readonly AutoResetEvent _collectionDue = new(false);
    private static Thread? _collector;

    // Only ever called from the frame, so the thread is made once.
    private static void RequestYoungCollection()
    {
        if (_collector == null)
        {
            _collector = new Thread(CollectWhenDue) { IsBackground = true, Name = "GC pacer", Priority = ThreadPriority.AboveNormal };
            _collector.Start();
        }
        _collectionDue.Set();
    }

    private static void CollectWhenDue()
    {
        while (true)
        {
            _collectionDue.WaitOne();
            GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        }
    }
}
