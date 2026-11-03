// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lumora.Core.Components.Scripting;
using Lumora.Script;

namespace Lumora.Core.Scripting;

// The script phase of a world's update: after links are granted, before components update, so a
// script's writes are in place before anything reads them this frame.
//
// Due timers join their script's queue, then every event waiting as the phase begins runs, one per script
// in turn; what those events queue runs next frame, so an event that raises another cannot hold the frame
// forever. Then every per-frame update runs, then every script's drives, then the Displays. Nothing is held back for time: a call that
// takes WarnCallMs, or a script whose calls take WarnFrameMs of one frame, is warned about and carries on.
// The one stop is ScriptInstanceOptions.MaxCallMilliseconds, for a call that never ends. A fault is marked
// on the node and warned about; the script keeps running.
//
// Compiles run on workers. A host waiting on one is looked at again here, first thing in the phase,
// once it has landed, so a script starts in the frame its compile is ready. -xlinka
public sealed class ScriptScheduler
{
    public const double WarnCallMs = 100.0;
    public const double WarnFrameMs = 16.0;

    private readonly World _world;
    private readonly HashSet<ScriptHost> _hosts = new();
    private readonly List<ScriptHostRuntime> _runtimes = new();
    private ScriptHostRuntime[] _snapshot = Array.Empty<ScriptHostRuntime>();
    private bool _snapshotDirty;

    // The snapshot split by what each pass walks, so a world of seven hundred scripts with thirty that run
    // every frame walks thirty for the frame's update, not seven hundred. What a runtime does is fixed when
    // it starts; the lists are made again with the snapshot. Index 0 is the Shared pass, 1 the Local.
    private readonly ScriptHostRuntime[][] _sides = { Array.Empty<ScriptHostRuntime>(), Array.Empty<ScriptHostRuntime>() };
    private readonly ScriptHostRuntime[][] _updaters = { Array.Empty<ScriptHostRuntime>(), Array.Empty<ScriptHostRuntime>() };
    private readonly ScriptHostRuntime[][] _drivers = { Array.Empty<ScriptHostRuntime>(), Array.Empty<ScriptHostRuntime>() };
    private readonly List<ScriptHostRuntime>[] _due = { new(), new() };
    private bool _recheck;
    private readonly List<(ScriptHost Host, System.Threading.Tasks.Task Compile)> _compiles = new();

    public double LastUpdateMs { get; private set; }

    public int Count => _runtimes.Count;

    public ScriptAuthorLedger Ledger { get; }

    public ScriptPhaseStats Phase { get; } = new();

    public int HostCount => _hosts.Count;

    // What a Shared script costs lives on the host. While a panel on another machine asks, the host
    // writes it onto each ScriptHost once a second; nobody asking costs nothing.
    public const double CostWatchSeconds = 10.0;
    private double _publishUntil = double.NegativeInfinity;
    private double _nextPublish;

    // What scripts recorded for people to undo: on the host, the batches it holds for each person's machine;
    // on a guest's machine, the relay that carries them.
    public ScriptUndoHub Undo { get; }

    // What scripts here asked of people at other machines: on the host, the asks it writes into each person's
    // relay; on a guest's machine, the relay that carries them.
    public ScriptUserAsks UserAsks { get; }

    // Packs one author's scripts start in a minute, the world's scripts between them, past which the script
    // packing is warned about. A packed script can pack again, and this is how that shows before it runs away.
    public const int WarnPacksPerMinute = 10;
    private readonly Dictionary<string, Queue<double>> _packTimes = new(StringComparer.Ordinal);
    private readonly List<(System.Threading.Tasks.Task Ready, Action Land)> _packs = new();

    public int PacksWaiting => _packs.Count;

    // The frame's update, raised through one delegate made once rather than a new one per script per frame.
    private FrameTick _tick;
    private readonly Func<ScriptRunner, ScriptCallResult> _raiseTick;

    internal ScriptScheduler(World world)
    {
        _raiseTick = runner => runner.Raise(_tick);
        _world = world;
        Ledger = new ScriptAuthorLedger(world);
        Undo = new ScriptUndoHub(world);
        UserAsks = new ScriptUserAsks(world);
        ScriptEnvironment.WarmUp();
    }

    // A pack a script started, set up in the script phase once its compile and its store have landed.
    internal void AwaitPack(System.Threading.Tasks.Task ready, Action land) => _packs.Add((ready, land));

    // Scripts a person's script packed, as the host set them up for it: what decides what each runs, and the
    // values the host wrote there. The host writes those for the author's script, so while they hold the same
    // values the writes are the author's doing; anything else the host's own person writes there is not.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScriptHost, List<(IField Field, object? Value)>> _setUps = new();

    internal void RememberSetUp(ScriptHost host, IEnumerable<IField> fields)
    {
        var held = new List<(IField, object?)>();
        foreach (var field in fields)
            held.Add((field, field.BoxedValue));
        _setUps.AddOrUpdate(host, held);
    }

    internal bool SetUpForAuthor(ScriptHost host, Networking.Sync.ConflictingSyncElement member)
    {
        if (!_world.IsAuthority || !ReferenceEquals(member.LastModifyingUser, _world.LocalUser) || !_setUps.TryGetValue(host, out var held))
            return false;
        foreach (var (field, value) in held)
        {
            if (ReferenceEquals(field, member))
                return Equals(field.BoxedValue, value);
        }
        return false;
    }

    // Counted as each pack starts.
    internal void Packed(ScriptContext ctx)
    {
        var key = ctx.Principal.AuthorKey;
        double now = _world.Time.TotalTime;
        if (!_packTimes.TryGetValue(key, out var times))
        {
            times = new Queue<double>();
            _packTimes[key] = times;
        }
        while (times.Count > 0 && now - times.Peek() >= 60.0)
            times.Dequeue();
        times.Enqueue(now);
        if (times.Count == WarnPacksPerMinute + 1)
            ctx.Warn(ScriptWarning.Packs, $"its author's scripts packed more than {WarnPacksPerMinute} scripts in a minute");
    }

    // Hosts waiting on a compile on a worker, taken up again once it lands.
    internal void AwaitCompile(ScriptHost host, System.Threading.Tasks.Task compile) => _compiles.Add((host, compile));

    public int CompilesWaiting => _compiles.Count;

    internal void Register(ScriptHost host) => _hosts.Add(host);

    internal void Unregister(ScriptHost host) => _hosts.Remove(host);

    internal void Add(ScriptHostRuntime runtime)
    {
        _runtimes.Add(runtime);
        _snapshotDirty = true;
    }

    internal void Remove(ScriptHostRuntime runtime)
    {
        if (_runtimes.Remove(runtime))
            _snapshotDirty = true;
    }

    // Rights changed or someone left: every host, running or not, decides again whether its script may run.
    public void ScheduleRecheck() => _recheck = true;

    // Every script host this machine knows in this world, running here or not.
    public void GetHosts(List<ScriptHost> into)
    {
        into.Clear();
        foreach (var host in _hosts)
        {
            if (!host.IsDestroyed)
                into.Add(host);
        }
    }

    internal void WatchCosts() => _publishUntil = _world.Time.TotalTime + CostWatchSeconds;

    public void Update(float delta)
    {
        long start = Stopwatch.GetTimestamp();

        for (int i = _compiles.Count - 1; i >= 0; i--)
        {
            var (host, compile) = _compiles[i];
            if (!compile.IsCompleted)
                continue;
            _compiles.RemoveAt(i);
            if (!host.IsDestroyed)
                host.Reconcile();
        }

        for (int i = _packs.Count - 1; i >= 0; i--)
        {
            var (ready, land) = _packs[i];
            if (!ready.IsCompleted)
                continue;
            _packs.RemoveAt(i);
            try
            {
                land();
            }
            catch (Exception ex)
            {
                Logging.Logger.Warn($"Loom: a packed script could not be set up: {ex.Message}");
            }
        }

        if (_recheck)
        {
            Undo.Recheck();
            _recheck = false;
            Ledger.DropMadeByAbsent();
            var hosts = new ScriptHost[_hosts.Count];
            _hosts.CopyTo(hosts);
            foreach (var host in hosts)
                host.Reconcile();
        }

        double now = _world.Time.TotalTime;
        if (_world.IsAuthority && now < _publishUntil && now >= _nextPublish)
        {
            _nextPublish = now + 1.0;
            foreach (var host in _hosts)
                host.PublishCost();
        }

        Undo.Update(now);
        UserAsks.Update(now);

        var runtimes = Snapshot();
        if (runtimes.Length == 0)
        {
            LastUpdateMs = 0;
            Phase.EndFrame(now, 0, 0, 0);
            return;
        }

        _due[0].Clear();
        _due[1].Clear();
        foreach (var runtime in runtimes)
        {
            runtime.QueueDueTimers(now);
            if (runtime.TakeDue())
                _due[runtime.IsLocal ? 1 : 0].Add(runtime);
        }

        double shared = Pass(runtimes, local: false, now, delta);
        double local = Pass(runtimes, local: true, now, delta);
        UserAsks.Flush();

        LastUpdateMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

        foreach (var runtime in runtimes)
        {
            runtime.Stats.EndFrame(now);
            if (runtime.FrameMs >= WarnFrameMs && runtime.CanRun)
                runtime.Runner.Context.Warn(ScriptWarning.HeavyFrame, $"its calls took {runtime.FrameMs:0.#} ms of one frame");
            runtime.FrameMs = 0;
        }
        Phase.EndFrame(now, LastUpdateMs, shared, local);
    }

    private double Pass(ScriptHostRuntime[] runtimes, bool local, double now, float delta)
    {
        double spent = 0;
        int side = local ? 1 : 0;

        var due = _due[side];
        bool progress = due.Count > 0;
        while (progress)
        {
            progress = false;
            foreach (var runtime in due)
            {
                if (!runtime.CanRun || !runtime.TryTakeDue())
                    continue;
                spent += Run(runtime, runtime.Dequeue());
                progress = true;
            }
        }

        var updaters = _updaters[side];
        if (updaters.Length > 0)
        {
            _tick = new FrameTick(delta);
            foreach (var runtime in updaters)
            {
                if (runtime.CanRun)
                    spent += Run(runtime, _raiseTick);
            }
        }

        foreach (var runtime in _drivers[side])
        {
            if (!runtime.CanRun)
                continue;
            if (!runtime.Runner.DrivesCurrent)
                spent += Run(runtime, static runner => runner.RunDrives());
            if (runtime.CanRun)
                runtime.ApplyDrives(now);
        }

        if (!local)
        {
            foreach (var runtime in _sides[side])
            {
                if (runtime.CanRun && runtime.DisplaysDue(now))
                    spent += Refresh(runtime, now);
            }
        }
        return spent;
    }

    private double Run(ScriptHostRuntime runtime, Func<ScriptRunner, ScriptCallResult> call)
    {
        long before = Stopwatch.GetTimestamp();
        var context = runtime.Runner.Context;
        context.BeginCall();
        // One writer scope for the call, not one per node call inside it.
        bool entered = context.EnterWholeCall(out var mark);
        ScriptCallResult result;
        try
        {
            result = call(runtime.Runner);
        }
        finally
        {
            ScriptContext.LeaveWholeCall(entered, mark);
        }
        context.EndCall();
        context.RunOwnDestroy();
        double ms = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;

        runtime.FrameMs += ms;
        runtime.Stats.Record(ms, result.FuelUsed, result.Status);
        Phase.Call(result.FuelUsed);
        if (ms >= WarnCallMs)
            context.Warn(ScriptWarning.SlowCall, $"one call took {ms:0} ms");

        if (runtime.Runner.Stopped)
            runtime.Host.RuntimeStopped(runtime, result.Message ?? "stopped");
        else if (result.Status is ScriptCallStatus.Trap or ScriptCallStatus.OutOfFuel or ScriptCallStatus.OutOfTime)
            runtime.RecordFault(_world.Time.TotalTime, result);
        return ms;
    }

    // A Display refresh takes the frame's time like any call, but what goes wrong in it is shown on the
    // Displays rather than counted against the script they watch.
    private double Refresh(ScriptHostRuntime runtime, double now)
    {
        long before = Stopwatch.GetTimestamp();
        runtime.PublishDisplays(now);
        double ms = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;

        runtime.FrameMs += ms;
        runtime.Stats.Record(ms, 0, ScriptCallStatus.Ok, call: false);
        if (runtime.Runner.Stopped)
            runtime.Host.RuntimeStopped(runtime, "stopped");
        return ms;
    }

    private ScriptHostRuntime[] Snapshot()
    {
        if (_snapshotDirty)
        {
            _snapshot = _runtimes.ToArray();
            _snapshotDirty = false;
            for (int side = 0; side < 2; side++)
            {
                bool local = side == 1;
                _sides[side] = Array.FindAll(_snapshot, runtime => runtime.IsLocal == local);
                _updaters[side] = Array.FindAll(_sides[side], runtime => runtime.WantsUpdate);
                _drivers[side] = Array.FindAll(_sides[side], runtime => runtime.DrivesHere);
            }
        }
        return _snapshot;
    }
}
