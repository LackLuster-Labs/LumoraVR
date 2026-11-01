// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// ONE WORLD'S FLUID, AS THIS MACHINE RUNS IT
//
// A plain object in a static table keyed by world, made by the first fluid part to start in that world (a system, an
// emitter, a tool, a sponge) and dropped in World.WhenDestroyed, which also frees the world's GPU buffers at once: under
// the Safe thread model that callback runs on the main thread, which is the render thread, and waiting for the next
// FramePreDraw would leak until a minimized window came back. A world with no fluid never makes one and pays nothing.
//
// It owns a local slot, "Fluids (local)", under the world root: the driver that runs the fluid pass, the residue and
// stain renderers whose hooks start the module's Godot half, and the two footing brackets. Local slots are never saved
// or sent. It also keeps the wearer's own parts (FluidPerson, FluidContextActions) on the local user's root, and puts
// them back within a frame of a respawn, which tears the old root down and everything on it.
//
// The other lanes plug in through the events below rather than through this file: Created for per-world helpers,
// Updating for gathers that read the summary, SummaryArrived for each summary as it lands, Packing to add their
// records to the frame block before it is submitted, LocalRootChanged for a respawn. All of them run on the world
// thread inside this world's update.
//
// One coordinator. The ledger lane's FluidLedgerWorld runs inside this space through UseHost: the space keeps the
// mirrors, the permission rule, the spawn fills, the sensors, the 4 Hz pass and the command ring writer; the
// coordinator keeps the election, everyone's rings, the judges, the shares and the keeper's reports, and hands each
// GPU machine its commands once (TakeCommands), so the ring has one reader. -xlinka
public sealed class FluidSpace
{
    public const string SlotName = "Fluids (local)";
    public const int MaxSystems = FluidLayout.SummarySystemsCapacity;
    public const double SensorInterval = 0.25;
    public const double ContainerWriteInterval = 1.0;

    private static readonly Dictionary<World, FluidSpace> Spaces = new();
    private static readonly object SpacesLock = new();

    private readonly List<FluidSystem> _systems = new();
    private readonly List<FluidEmitter> _emitters = new();
    private readonly List<FluidSpawnVolume> _spawnVolumes = new();
    private readonly List<FluidDrain> _drains = new();
    private readonly List<FluidSensor> _sensors = new();
    private readonly List<FluidForce> _forces = new();
    private readonly List<FluidContainer> _containers = new();
    private readonly List<Component> _parts = new();
    private readonly FluidSystemStatus[] _status = new FluidSystemStatus[MaxSystems];
    private readonly double[] _containerWrittenAt = new double[MaxSystems];
    private readonly Dictionary<FluidSystem, FluidLedger> _ledgers = new();
    private readonly List<(FluidSystem System, FluidCommand Command)> _commands = new();
    private readonly List<User> _users = new();
    private readonly List<float3> _people = new();
    private ulong _frame;
    private double _nextSensorAt;
    private double _nextPassAt;
    private double _nextLitresAt;
    private UserRoot? _lastRoot;
    private bool _warnedSystems;
    private bool _pathLogged;
    private int _failedHandlers;
    private readonly List<FluidLedgerEntry> _binRound = new();
    private double _nextLedgerCheckAt;
    private readonly Dictionary<FluidSystem, List<int4>> _binsBySystem = new();
    private bool _binRoundOpen;
    private volatile bool _wake;

    public static event Action<FluidSpace>? Created;

    // Frame pressure is diagnostic only; it cannot lower the user's fluid quality. -xlinka
    public static Func<bool>? FramePressed { get; set; }

    public static int Count
    {
        get
        {
            lock (SpacesLock)
                return Spaces.Count;
        }
    }

    private FluidSpace(World world)
    {
        World = world;
        Budget = new FluidBudget(FluidGpuTier.None, world.Name);
        Packer = new FluidFramePacker(this);
        Colliders = new FluidColliderGather(this);
    }

    public World World { get; }

    public Slot? LocalSlot { get; private set; }

    public FluidSpaceDriver? Driver { get; private set; }

    // This world's simulation on the device; null on a view-only machine, a headless host, or while the path is
    // still undecided.
    public IFluidGpuWorld? Gpu { get; private set; }

    public FluidBudget Budget { get; }

    public FluidFramePacker Packer { get; }

    public FluidColliderGather Colliders { get; }

    // The ledger lane's coordinator for this world, run through UseHost.
    public FluidLedgerWorld? Ledgers { get; private set; }

    // Complete bin rounds handed to the coordinator, and the pages dropped for arriving out of order.
    public int BinRounds { get; private set; }

    public int BinPagesDropped { get; private set; }

    public bool IsDestroyed { get; private set; }

    public IReadOnlyList<FluidSystem> Systems => _systems;

    public IReadOnlyList<FluidEmitter> Emitters => _emitters;

    public IReadOnlyList<FluidSpawnVolume> SpawnVolumes => _spawnVolumes;

    public IReadOnlyList<FluidDrain> Drains => _drains;

    public IReadOnlyList<FluidSensor> Sensors => _sensors;

    public IReadOnlyList<FluidForce> Forces => _forces;

    public IReadOnlyList<FluidContainer> Containers => _containers;

    // World updates this space has run.
    public ulong Frame => _frame;

    // The device check has answered (or there is no device at all, which is a headless host's answer). Until then the
    // space publishes no tier and no view-only state: Pending is undecided, not "no GPU".
    public bool PathDecided => FluidGpu.Current == null || FluidGpu.Current.Path != FluidGpuPath.Pending;

    // The selected tier on this machine; None when this world does not simulate here. -xlinka
    public FluidGpuTier Tier => Gpu != null ? Budget.Tier : FluidGpuTier.None;

    public bool Simulating => Gpu != null && !Budget.View;

    public bool ViewOnly => PathDecided && !Simulating;

    // Keeper candidacy requires active simulation and a renderer that is still drawing. -xlinka
    public bool CanKeep => Simulating && Budget.Healthy && FluidGpu.Current?.DrawingInRealTime == true;

    public bool Focused => World.Focus != World.WorldFocus.Background;

    // Bumped when systems are added or removed: particles carry their system's index, so a new order reseeds.
    public int LayoutVersion { get; private set; }

    public bool SystemsDirty { get; set; } = true;

    public bool SubstancesDirty { get; set; } = true;

    // The last summary that landed, and each system's status from it (by system index).
    public FluidSummaryHeader LastSummary { get; private set; }

    public int SummariesTaken { get; private set; }

    public ReadOnlySpan<FluidSystemStatus> Status => _status;

    public bool AllAsleep { get; private set; }

    public float Vmax { get; private set; }

    // The local user's root as last seen, the parts this machine keeps on it, and how often it came back new.
    public UserRoot? LocalRoot { get; private set; }

    public FluidPerson? LocalPerson { get; private set; }

    public FluidContextActions? LocalActions { get; private set; }

    public int Respawns { get; private set; }

    public event Action<FluidSpace>? LocalRootChanged;

    public event Action<FluidSpace>? Updating;

    public event Action<FluidSpace, FluidSummaryView>? SummaryArrived;

    public event Action<FluidSpace, FluidFrameBlock>? Packing;

    public event Action<FluidSpace>? Destroyed;

    public static FluidSpace? Find(World? world)
    {
        if (world == null)
            return null;
        lock (SpacesLock)
            return Spaces.TryGetValue(world, out var space) && !space.IsDestroyed ? space : null;
    }

    // The world's space, made on first use. Call it from that world's own update (a part's OnStart is).
    public static FluidSpace? Ensure(World? world)
    {
        var found = Find(world);
        if (found != null)
            return found;
        if (world == null || world.IsDestroyed || world.IsDisposed || world.IsUserspace || world.RootSlot == null)
            return null;

        var space = new FluidSpace(world);
        lock (SpacesLock)
        {
            if (Spaces.TryGetValue(world, out var raced) && !raced.IsDestroyed)
                return raced;
            Spaces[world] = space;
        }
        world.WhenDestroyed += space.OnWorldDestroyed;
        if (space.IsDestroyed)
            return null;
        space.StartLedgers();
        space.BuildLocalSlot();
        try
        {
            Created?.Invoke(space);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: a space helper failed to start in '{world.Name}': {ex.Message}");
        }
        return space;
    }

    // The coordinator takes the space's mirrors and command path, so it adds no rule and writes no ring of its own.
    private void StartLedgers()
    {
        try
        {
            var ledgers = FluidLedgerWorld.For(World);
            ledgers.UseHost(LedgerOf, (system, command) => Command(system, command));
            Ledgers = ledgers;
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: the ledger coordinator failed to start in '{World.Name}': {ex.Message}");
        }
    }

    // Found by its component, never by name alone: a slot of the world's own that happens to carry the name is
    // somebody's content.
    private void BuildLocalSlot()
    {
        var root = World.RootSlot;
        Slot? slot = null;
        foreach (var child in root.LocalChildren)
        {
            if (child is { IsDestroyed: false } && child.GetComponent<FluidSpaceDriver>() is { IsDestroyed: false })
            {
                slot = child;
                break;
            }
        }
        if (slot == null)
        {
            slot = root.AddLocalSlot(SlotName);
            slot.Persistent.Value = false;
            slot.GetOrAttachComponent<ImmutableComponent>();
        }
        LocalSlot = slot;
        Driver = slot.GetOrAttachComponent<FluidSpaceDriver>();
        slot.GetOrAttachComponent<FluidResidueRenderer>();
        slot.GetOrAttachComponent<FluidStainPool>();
        slot.GetOrAttachComponent<FluidFootingApply>();
        slot.GetOrAttachComponent<FluidFootingRestore>();
    }

    internal void Add(Component part)
    {
        if (IsDestroyed || part == null || part.IsDestroyed || _parts.Contains(part))
            return;
        _parts.Add(part);
        switch (part)
        {
            case FluidSystem system:
                if (_systems.Count >= MaxSystems)
                {
                    if (!_warnedSystems)
                    {
                        _warnedSystems = true;
                        LumoraLogger.Warn($"Fluids: '{World.Name}' has more than {MaxSystems} fluid systems; the rest are not simulated");
                    }
                    break;
                }
                _systems.Add(system);
                LayoutVersion++;
                SystemsDirty = true;
                SubstancesDirty = true;
                Guard(() => Ledgers?.Track(system), "tracking a system");
                break;
            case FluidEmitter emitter:
                _emitters.Add(emitter);
                Guard(() => Ledgers?.TrackEmitter(emitter), "tracking an emitter");
                break;
            case FluidSpawnVolume volume:
                _spawnVolumes.Add(volume);
                break;
            case FluidDrain drain:
                _drains.Add(drain);
                break;
            case FluidSensor sensor:
                _sensors.Add(sensor);
                break;
            case FluidForce force:
                _forces.Add(force);
                break;
            case FluidContainer container:
                _containers.Add(container);
                SystemsDirty = true;
                break;
        }
    }

    internal void Remove(Component part)
    {
        if (part == null || !_parts.Remove(part))
            return;
        switch (part)
        {
            case FluidSystem system:
                if (_systems.Remove(system))
                {
                    LayoutVersion++;
                    SystemsDirty = true;
                    SubstancesDirty = true;
                }
                _ledgers.Remove(system);
                LedgerRule?.Forget(system);
                _binsBySystem.Remove(system);
                Guard(() => Ledgers?.Untrack(system), "untracking a system");
                break;
            case FluidEmitter emitter:
                _emitters.Remove(emitter);
                Packer.Forget(emitter);
                break;
            case FluidSpawnVolume volume:
                _spawnVolumes.Remove(volume);
                break;
            case FluidDrain drain:
                _drains.Remove(drain);
                break;
            case FluidSensor sensor:
                _sensors.Remove(sensor);
                break;
            case FluidForce force:
                _forces.Remove(force);
                break;
            case FluidContainer container:
                _containers.Remove(container);
                SystemsDirty = true;
                break;
        }
    }

    public int SystemIndex(FluidSystem? system) => system == null ? -1 : _systems.IndexOf(system);

    public FluidSystem? SystemAt(int index) => (uint)index < (uint)_systems.Count ? _systems[index] : null;

    public FluidContainer? ContainerOf(FluidSystem? system)
    {
        if (system == null)
            return null;
        foreach (var container in _containers)
        {
            if (!container.IsDestroyed && container.Contents.Target == system)
                return container;
        }
        return null;
    }

    public ref readonly FluidSystemStatus StatusOf(int systemIndex)
    {
        if ((uint)systemIndex >= (uint)_status.Length)
            throw new ArgumentOutOfRangeException(nameof(systemIndex));
        return ref _status[systemIndex];
    }

    // The fluid pass's first half, from FluidSpaceDriver.OnUpdate.
    internal void Update(float delta)
    {
        if (IsDestroyed)
            return;
        _frame++;
        CheckLocalRoot();
        RefreshGpu();
        TakeSummaries();
        if (Gpu != null && World.Time.TotalTime >= _nextLedgerCheckAt)
        {
            _nextLedgerCheckAt = World.Time.TotalTime + 1.0;
            Budget.ReseedHeld = LedgerBehind();
        }
        // On View nothing runs and no timestamp comes back; nothing running costs nothing.
        if (Gpu != null && Budget.View && Budget.MachineTier != FluidGpuTier.None)
            Budget.Report(0, World.Time.TotalTime);
        // The coordinator first: what it judges this update (requests, shares) reaches the ring in the same update.
        if (Ledgers != null)
        {
            try
            {
                Ledgers.Update();
            }
            catch (Exception ex)
            {
                Failed(16, "the ledger coordinator", ex);
            }
        }
        if (World.IsAuthority)
            RunHostTasks();
        if (Simulating)
            Colliders.Gather(_frame);
        Raise(Updating, 2, "an update handler");
    }

    // The fluid pass's second half: pack the frame block and hand it to the device. Late update in a focused world,
    // after held things have their final pose; the update itself in a background world, whose late update the world
    // manager skips.
    internal void Pack(float delta)
    {
        var gpu = Gpu;
        if (IsDestroyed || gpu == null || !Simulating)
            return;
        // Holds: a world that has never run waits out its load, and a mesh collider's grid gets half a second.
        if ((World.IsLoading && !Packer.HasRun) || Colliders.Holding)
            return;
        var block = gpu.Block;
        Packer.Pack(block, delta);
        if (_wake)
        {
            _wake = false;
            Packer.WakeAll(block);
        }
        try
        {
            Packing?.Invoke(this, block);
        }
        catch (Exception ex)
        {
            Failed(4, "a packing handler", ex);
        }
        Packer.Finish(block);
        gpu.Submit();
    }

    // This machine's simulation went stale: its world kept updating while nothing was drawn (a stopped render loop), so
    // the blocks of that stretch were never stepped and everyone else moved on. The next pack drops the loose fluid
    // and lays every system in again from the ledger (the packer's whole-system reseed), before it bins again.
    public void ReseedFromLedger()
    {
        Packer.Opened();
        StaleReseeds++;
    }

    public int StaleReseeds { get; private set; }

    // The fluid changed behind the frame block's back (a seed straight into the solver), so the last summary's "all
    // asleep" no longer holds: the next pack wakes every system with a wake box round all of it, which also steps it.
    public void Wake()
    {
        AllAsleep = false;
        _wake = true;
    }

    private void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Failed(32, what, ex);
        }
    }

    // Another lane's handler that throws must not take this world's update down with it; each kind is logged once.
    private void Raise(Action<FluidSpace>? handlers, int bit, string what)
    {
        if (handlers == null)
            return;
        try
        {
            handlers(this);
        }
        catch (Exception ex)
        {
            Failed(bit, what, ex);
        }
    }

    private void Failed(int bit, string what, Exception ex)
    {
        if ((_failedHandlers & bit) != 0)
            return;
        _failedHandlers |= bit;
        LumoraLogger.Warn($"Fluids: {what} failed in '{World.Name}': {ex.Message}");
    }

    private void CheckLocalRoot()
    {
        var user = World.LocalUser;
        var root = user?.Root;
        if (root != null && (root.IsDestroyed || root.Slot == null || root.Slot.IsDestroyed))
            root = null;
        bool changed = !ReferenceEquals(root, LocalRoot);
        bool lost = root != null && (LocalPerson == null || LocalPerson.IsDestroyed || LocalActions == null || LocalActions.IsDestroyed);
        if (!changed && !lost)
            return;
        LocalRoot = root;
        if (root == null)
        {
            LocalPerson = null;
            LocalActions = null;
        }
        else
        {
            AttachWearerParts(user!, root);
        }
        // A new root after another one is a respawn, whether or not a frame without one came between.
        if (changed && root != null)
        {
            if (_lastRoot != null && !ReferenceEquals(_lastRoot, root))
                Respawns++;
            _lastRoot = root;
        }
        if (changed)
            Raise(LocalRootChanged, 1, "a respawn handler");
    }

    // Only the wearer's machine attaches these, to its own root, through the people lane's one path: it trusts only a
    // FluidPerson the wearer minted (an editor with consent could attach a forged one), and sizes the new one's arrays.
    private void AttachWearerParts(User user, UserRoot root)
    {
        LocalPerson = FluidPerson.Ensure(World);
        LocalActions = root.Slot.GetComponent<FluidContextActions>();
    }

    private void RefreshGpu()
    {
        var current = FluidGpu.Current;
        if (current == null || current.Path == FluidGpuPath.Pending)
            return;
        if (!_pathLogged)
        {
            _pathLogged = true;
            if (current.Path != FluidGpuPath.Gpu)
                LumoraLogger.Log($"Fluids: '{World.Name}' is view only here ({current.Status})");
        }
        var machine = current.Path == FluidGpuPath.Gpu ? current.Tier : FluidGpuTier.None;
        int tierVersion = Budget.TierVersion;
        Budget.SetMachineTier(machine);
        if (Budget.TierVersion != tierVersion)
            SubstancesDirty = SystemsDirty = true;
        if (machine != FluidGpuTier.None && Gpu == null)
        {
            Gpu = current.Open(World);
            if (Gpu != null)
            {
                SystemsDirty = true;
                SubstancesDirty = true;
                Packer.Opened();
            }
        }
        else if (machine == FluidGpuTier.None && Gpu != null)
        {
            Gpu = null;
            current.Close(World);
        }
    }

    private void TakeSummaries()
    {
        var gpu = Gpu;
        if (gpu == null)
            return;
        while (gpu.TryTakeSummary(out var summary))
        {
            try
            {
                Consume(gpu, summary);
                SummaryArrived?.Invoke(this, summary);
            }
            catch (Exception ex)
            {
                Failed(8, "a summary handler", ex);
            }
            finally
            {
                gpu.ReturnSummary(summary);
            }
        }
    }

    private void Consume(IFluidGpuWorld gpu, FluidSummaryView summary)
    {
        if (summary.IsEmpty)
            return;
        var header = summary.Header;
        LastSummary = header;
        SummariesTaken++;
        AllAsleep = (header.Flags & FluidLayout.SummaryAllAsleep) != 0 || header.Awake == 0;
        Vmax = float.IsFinite(header.Vmax) ? MathF.Max(0f, header.Vmax) : 0f;

        Array.Clear(_status);
        var systems = summary.Systems;
        for (int i = 0; i < systems.Length && i < _status.Length; i++)
            _status[i] = systems[i];

        Budget.Report(gpu.LastGpuMs, World.Time.TotalTime, FramePressed?.Invoke() ?? true);

        TakeBins(summary, header);

        // Drained litres and container litres from this machine's own simulation, written on the host when it keeps
        // the system itself. A guest keeper's drains go to the coordinator, which reports them to the host.
        if (!World.IsAuthority)
        {
            OfferDrains(summary);
            return;
        }
        foreach (var volume in summary.Volumes)
        {
            if (!float.IsFinite(volume.Litres) || volume.Litres < 0f)
                continue;
            if (volume.Kind == FluidFramePacker.VolumeDrain && volume.Index < (uint)_drains.Count)
            {
                var drain = _drains[(int)volume.Index];
                if (!drain.IsDestroyed && volume.Litres > 0f && KeptHere(drain.System.Target))
                    drain.Drained.Value += volume.Litres;
            }
            else if (volume.Kind == FluidFramePacker.VolumeContainer && volume.Index < (uint)_containers.Count)
            {
                var container = _containers[(int)volume.Index];
                int slot = (int)volume.Index;
                if (container.IsDestroyed || !KeptHere(container.Contents.Target) || slot >= _containerWrittenAt.Length)
                    continue;
                double now = World.Time.TotalTime;
                if (now - _containerWrittenAt[slot] < ContainerWriteInterval && MathF.Abs(container.Litres.Value - volume.Litres) < 0.01f)
                    continue;
                _containerWrittenAt[slot] = now;
                container.Litres.Value = volume.Litres;
            }
        }
    }

    // The keeper's bins, a page a summary (192 a page): a round starts at cursor 0 and is whole when it holds
    // BinTotal entries; then each system this machine keeps gets its share, an empty one included, since a complete
    // round with nothing for a system means its fluid is gone. A page out of order drops the round. Only a summary the
    // bin kernel flagged (FluidBinRound.Carries) holds a page, so a round that binned nothing (BinTotal 0 on its one
    // page) is a whole, empty round, and a summary with no flag is no round at all.
    private void TakeBins(in FluidSummaryView summary, in FluidSummaryHeader header)
    {
        var bins = summary.Bins;
        if (!FluidBinRound.Carries(header))
            return;
        if (header.BinCursor == 0)
        {
            _binRound.Clear();
            _binRoundOpen = true;
        }
        else if (!_binRoundOpen || header.BinCursor != _binRound.Count)
        {
            _binRoundOpen = false;
            BinPagesDropped++;
            return;
        }
        foreach (var bin in bins)
            _binRound.Add(bin);
        if (_binRound.Count < header.BinTotal)
            return;
        _binRoundOpen = false;
        BinRounds++;
        foreach (var list in _binsBySystem.Values)
            list.Clear();
        foreach (var bin in _binRound)
        {
            var system = SystemAt((int)bin.System);
            if (system == null)
                continue;
            if (!_binsBySystem.TryGetValue(system, out var list))
                _binsBySystem[system] = list = new List<int4>();
            list.Add(bin.Cell);
        }
        _binRound.Clear();
        if (Ledgers == null)
            return;
        foreach (var system in _systems)
        {
            if (!system.IsKeeperHere)
                continue;
            int4[] cells = _binsBySystem.TryGetValue(system, out var list) ? list.ToArray() : Array.Empty<int4>();
            Guard(() => Ledgers.OfferBins(system, cells), "offering bins");
        }
    }

    // A guest keeper's drained litres, as Drained events (the drain's RefID, then microlitres).
    private void OfferDrains(in FluidSummaryView summary)
    {
        if (Ledgers == null)
            return;
        foreach (var volume in summary.Volumes)
        {
            if (volume.Kind != FluidFramePacker.VolumeDrain || volume.Index >= (uint)_drains.Count || !(volume.Litres > 0f) || !float.IsFinite(volume.Litres))
                continue;
            var drain = _drains[(int)volume.Index];
            var system = drain.System.Target ?? FirstKept();
            if (drain.IsDestroyed || system == null || !system.IsKeeperHere)
                continue;
            ulong id = drain.ReferenceID.RawValue;
            int microlitres = (int)Math.Min(int.MaxValue, Math.Round(volume.Litres * 1e6));
            var payload = new int4(FluidRings.Low(id), FluidRings.High(id), microlitres, 0);
            Guard(() => Ledgers.OfferEvent(system, FluidReportKind.Drained, payload), "offering a drain");
        }
    }

    private FluidSystem? FirstKept()
    {
        foreach (var s in _systems)
        {
            if (s.IsKeeperHere)
                return s;
        }
        return null;
    }

    // True while some system simulates clearly more fluid here than its cells hold: the keeper has not binned it yet
    // (or nothing bins at all), so starting again from the cells would delete it. Checked once a second.
    private bool LedgerBehind()
    {
        if (SummariesTaken == 0)
            return false;
        var tier = Tier;
        for (int i = 0; i < _systems.Count && i < _status.Length; i++)
        {
            var system = _systems[i];
            if (system.IsDestroyed)
                continue;
            float d = system.SpacingOn(tier);
            double simulated = _status[i].Live * (double)d * d * d * 1000.0;
            if (simulated > 0.05 && system.LedgerLitres() < 0.8 * simulated)
                return true;
        }
        return false;
    }

    // A null system means any: a drain with no system takes from every system this machine keeps.
    private bool KeptHere(FluidSystem? system)
    {
        if (system != null)
            return system.IsKeeperHere;
        foreach (var s in _systems)
        {
            if (s.IsKeeperHere)
                return true;
        }
        return false;
    }

    // THE HOST'S SIDE. The ledger's per-system mirrors, the permission rule that guards the saved ledger, spawn fills,
    // the 4 Hz mirror pass, sensors and the command ring all live here, so every host runs them, a headless one
    // included. The ledger lane's logic does the work; this only gives it a world to do it in.

    // The host's mirror of a system's ledger, made on first use; null on a guest.
    public FluidLedger? LedgerOf(FluidSystem? system)
    {
        if (system == null || system.IsDestroyed || !World.IsAuthority || IsDestroyed)
            return null;
        if (_ledgers.TryGetValue(system, out var ledger))
            return ledger;
        ledger = new FluidLedger(system, EnsureLedgerRule());
        _ledgers[system] = ledger;
        return ledger;
    }

    // The one rule per world that lets only the host (and a system's creator for its first two seconds) write the
    // saved ledger members.
    public FluidLedgerRule? LedgerRule { get; private set; }

    private FluidLedgerRule? EnsureLedgerRule()
    {
        if (LedgerRule != null || !World.IsAuthority)
            return LedgerRule;
        var permissions = World.DataModelPermissions;
        if (permissions == null)
            return null;
        LedgerRule = new FluidLedgerRule(World);
        permissions.AddRule(LedgerRule);
        return LedgerRule;
    }

    // Host: a command every GPU machine applies once. The ring is sized in a send of its own before its first entry,
    // so a command asked for in the frame the ring is made waits one update.
    // point is in the system's own space, as the ring carries it; a seed of 0 takes one from the frame.
    public void Command(FluidSystem system, FluidCommandKind kind, float litres = 0f, float3 point = default, float radius = 0f, int origin = FluidCommand.AnyOrigin, int substance = 0, float3 velocity = default, int seed = 0)
    {
        if (system == null || system.IsDestroyed || !World.IsAuthority || IsDestroyed)
            return;
        if (seed == 0)
            seed = unchecked((int)((_frame + (ulong)_commands.Count + 1) * 2654435761UL));
        _commands.Add((system, new FluidCommand(kind, origin, point, radius, litres, substance, seed, velocity)));
    }

    // The same, for a command already made (a Loom emit, the ledger lane's coordinator): kind, origin, substance,
    // velocity and seed go on the ring as they are; a seed of 0 still takes one from the frame.
    public void Command(FluidSystem system, in FluidCommand command)
    {
        if (command.Seed != 0)
        {
            if (system == null || system.IsDestroyed || !World.IsAuthority || IsDestroyed)
                return;
            _commands.Add((system, command));
            return;
        }
        Command(system, command.Kind, command.Litres, command.Point, command.Radius, command.Origin, command.Substance, command.Velocity);
    }

    private void RunHostTasks()
    {
        foreach (var system in _systems)
            EnsureLedgerRule()?.Note(system);
        if (World.IsLoading)
            return;
        WriteCommands();
        foreach (var volume in _spawnVolumes)
            volume.FillOnHost(this);

        double now = World.Time.TotalTime;
        if (now >= _nextPassAt && _ledgers.Count > 0)
        {
            _nextPassAt = now + SensorInterval;
            int rate = World.RootSlot?.GetComponent<FluidWorldRules>()?.LedgerBytesPerSecond.Value ?? 4096;
            CollectPeople();
            foreach (var (system, ledger) in _ledgers)
            {
                if (!system.IsDestroyed)
                    ledger.Pass(now, rate, _people);
            }
        }
        if (now >= _nextSensorAt)
        {
            _nextSensorAt = now + SensorInterval;
            foreach (var sensor in _sensors)
                sensor.MeasureOnHost(this);
        }
        if (now >= _nextLitresAt)
        {
            _nextLitresAt = now + 1.0;
            using (World.DataModelPermissions?.EnterSystemBypass())
            {
                foreach (var system in _systems)
                {
                    float litres = (float)system.LedgerLitres();
                    if (MathF.Abs(system.Litres.Value - litres) > 1e-4f)
                        system.Litres.Value = litres;
                }
            }
        }
    }

    private void WriteCommands()
    {
        if (_commands.Count == 0)
            return;
        using var bypass = World.DataModelPermissions?.EnterSystemBypass();
        for (int i = 0; i < _commands.Count; i++)
        {
            var (system, command) = _commands[i];
            if (system.IsDestroyed)
            {
                _commands.RemoveAt(i--);
                continue;
            }
            if (system.Commands.Count != FluidSystem.CommandRingLength)
            {
                FluidRings.EnsureLength(system.Commands, FluidSystem.CommandRingLength);
                continue;
            }
            command.Write(out var a, out var b);
            FluidRings.Write(system.Commands, system.CommandSequence, a, b);
            _commands.RemoveAt(i--);
        }
    }

    private void CollectPeople()
    {
        _people.Clear();
        World.GetAllUsers(_users);
        foreach (var user in _users)
        {
            if (user?.Root is { IsDestroyed: false } root)
                _people.Add(root.HeadPosition);
        }
    }

    private void OnWorldDestroyed(World world)
    {
        if (IsDestroyed)
            return;
        IsDestroyed = true;
        lock (SpacesLock)
        {
            if (Spaces.TryGetValue(world, out var space) && ReferenceEquals(space, this))
                Spaces.Remove(world);
        }
        // Frees the world's buffers now, minimized window or not; the device defers the real release until the GPU
        // is done with the frame.
        try
        {
            FluidGpu.Current?.Close(world);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: closing '{world.Name}' on the device failed: {ex.Message}");
        }
        Gpu = null;
        try
        {
            Destroyed?.Invoke(this);
        }
        catch (Exception ex)
        {
            LumoraLogger.Warn($"Fluids: a space helper failed to stop in '{world.Name}': {ex.Message}");
        }
        if (LedgerRule != null)
        {
            try
            {
                World.DataModelPermissions?.RemoveRule(LedgerRule);
            }
            catch (Exception)
            {
                // The world's permissions may already be torn down with it.
            }
            LedgerRule = null;
        }
        _ledgers.Clear();
        _commands.Clear();
        _parts.Clear();
        _systems.Clear();
        _emitters.Clear();
        _spawnVolumes.Clear();
        _drains.Clear();
        _sensors.Clear();
        _forces.Clear();
        _containers.Clear();
        Packer.Clear();
        Colliders.Clear();
        LocalRoot = null;
        _lastRoot = null;
        LocalPerson = null;
        LocalActions = null;
        LocalRootChanged = null;
        Updating = null;
        SummaryArrived = null;
        Packing = null;
        Destroyed = null;
    }
}
