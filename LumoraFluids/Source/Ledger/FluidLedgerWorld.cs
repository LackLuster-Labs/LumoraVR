// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One world's ledger work on this machine, called once per update from the world's fluid pass. On the host it mirrors
// every system's cells, elects the keeper, reads everyone's rings (requests, pours, releases, the keeper's report),
// judges them, writes commands and keeps the shares. On the keeper it turns its own bins into reports and coat
// proposals. On every machine it hands out each system's commands once, by sequence. It holds no Godot state and
// simulates nothing, so a headless host runs all of it.
//
// What it needs from the rest of the module: Track each system as it wakes (and Untrack it), TrackEmitter for
// emitters, Spawn for a spawn volume due to fill, OfferBins with a complete set of bins per system on the machine that
// keeps it, OfferEvent for drains and prop touches, and TakeCommands before packing a frame.
//
// Standing alone it also owns each system's mirror, the permission rule, the command ring and the 4 Hz passes. When
// the world's fluid space already runs those, UseHost hands it the space's mirrors and command path instead, and this
// keeps to the election, the rings and the judges.
//
// On a GPU machine it also runs the ledger's side of the simulation through the space's events: every round of this
// machine's own bins (FluidBinRound), the reseeds (FluidReseedPlan: a whole system when the packer asks, the cells the
// host wrote on its own that the keeper never held, the reconcile's refills) and the reconcile toward the ledger
// (FluidReconcile) for every system it does not keep. The GPU half (FluidLedgerKernels) reads GpuSystems and Actions.
// -xlinka
public sealed class FluidLedgerWorld
{
    public const double PassInterval = 0.25;
    public const double SlowInterval = 1.0;

    private const int RingReleases = 0;
    private const int RingRequests = 1;
    private const int RingReport = 2;

    private static readonly Dictionary<World, FluidLedgerWorld> _all = new();

    private readonly List<FluidSystem> _systems = new();
    private readonly Dictionary<FluidSystem, FluidLedger> _ledgers = new();
    private readonly Dictionary<FluidSystem, FluidCommandWriter> _writers = new();
    private readonly Dictionary<FluidSystem, FluidReportJudge> _judges = new();
    private readonly Dictionary<FluidSystem, int> _commandCursors = new();
    private readonly Dictionary<FluidSystem, List<FluidCommand>> _local = new();
    private readonly List<FluidEmitter> _emitters = new();
    private readonly List<FluidSensor> _sensors = new();
    private readonly Dictionary<(FluidPerson, int), int> _cursors = new();
    private readonly Dictionary<User, Dictionary<(ulong, int), List<FluidReportEntry>>> _partial = new();
    private readonly Dictionary<(User, int), int> _pourSeen = new();
    private readonly List<User> _users = new();
    private readonly List<float3> _people = new();
    private readonly List<(int4 A, int4 B)> _raw = new();
    private readonly List<FluidCommand> _commands = new();
    private double _lastUpdate = double.NaN;
    private double _lastPass = double.NegativeInfinity;
    private double _lastSlow = double.NegativeInfinity;
    private FluidWorldRules? _rules;
    private double _rulesAt = double.NegativeInfinity;
    private bool _keeping;
    private Func<FluidSystem, FluidLedger?>? _hostLedgers;
    private Action<FluidSystem, FluidCommand>? _hostCommands;
    private readonly List<FluidLedger> _ledgerList = new();
    private readonly List<FluidSystem> _kept = new();
    private readonly List<FluidReportEntry> _incoming = new();
    private readonly List<List<FluidReportEntry>> _complete = new();
    private readonly List<FluidReportEntry> _events = new();

    // The GPU side.
    public const int GpuSystemFloats = 16;
    public const int MaxLedgerChanges = 512;
    // Rounds a ledger change keeps a sleeping block stepping for, so the reconcile sees it twice and acts.
    public const int ReconcileRoundsAfterChange = 3;
    private readonly Dictionary<FluidSystem, HashSet<(int, int, int, int)>> _held = new();
    private readonly Dictionary<FluidSystem, int> _keptRounds = new();
    private readonly Dictionary<FluidSystem, Dictionary<(int, int, int, int), int4>> _foreignDone = new();
    private readonly Dictionary<FluidSystem, int4[]> _shadow = new();
    private readonly List<FluidLedgerEntry> _actions = new();
    private readonly List<FluidLedgerEntry> _roundActions = new();
    private readonly List<FluidGpuCommand> _wakes = new();
    private readonly List<int> _foreign = new();
    private readonly Dictionary<FluidSystem, int4[]> _offered = new();
    private readonly HashSet<FluidSystem> _inherited = new();
    private readonly Dictionary<FluidSystem, double[]> _changedAt = new();
    // A keeper taking over answers for cells older than this; newer ones its reconcile may not have reached.
    public const double InheritAfter = 3.0;
    private readonly Dictionary<(int, int, int, int), (int4 Cell, int Fill, int Lead)> _merged = new();
    private readonly Dictionary<int, double> _originBins = new();
    private readonly float[] _gpuSystems = new float[FluidSpace.MaxSystems * GpuSystemFloats];
    private int _gpuSystemCount;
    private int _wakeUntilRound = -1;
    private FluidSpace? _space;
    // The account as this machine saw it over the last AccountWindow seconds, per system: its spread is the flow.
    public const double AccountWindow = 2.0;
    private readonly Dictionary<FluidSystem, Queue<(double At, double Litres)>> _accounts = new();
    // Evaporate and FadeOrigin commands handed to the packer this frame, per system, in order (see Restore).
    private readonly Dictionary<FluidSystem, Queue<FluidCommand>> _handed = new();
    public const int MaxOffRounds = 20;
    private int _offRounds;

    private FluidLedgerWorld(World world)
    {
        World = world;
        Election = new FluidKeeperElection(world);
        Requests = new FluidRequestJudge(world);
        Fairness = new FluidFairness(world);
        Report = new FluidKeeperReport(world);
        if (world.IsAuthority)
        {
            Rule = new FluidLedgerRule(world);
            world.DataModelPermissions?.AddRule(Rule);
            world.UserRemoved += OnUserRemoved;
        }
        world.WhenDestroyed += Drop;
    }

    public World World { get; }

    public FluidLedgerRule? Rule { get; private set; }

    // The world's fluid space runs the mirrors, the rule, the command ring and the passes; this only reads and judges.
    public bool Hosted => _hostLedgers != null;

    public FluidKeeperElection Election { get; }

    public FluidRequestJudge Requests { get; }

    public FluidFairness Fairness { get; }

    public FluidKeeperReport Report { get; }

    public FluidPickupBook Pickups { get; } = new();

    public FluidCoatProposals Proposals { get; } = new();

    public IReadOnlyList<FluidSystem> Systems => _systems;

    // Prop touches and snaps from the keeper's reports, on the host, for Loom.
    public event Action<FluidSystem, FluidReportEntry>? PropEvent;

    public int ReportsDropped { get; private set; }

    public int ForeignReportsIgnored { get; private set; }

    // Command ring entries this machine missed because it fell further behind than the ring holds.
    public int RingGaps { get; private set; }

    // GPU machines: this machine's own bin rounds, its reseeds and its reconcile.
    public FluidBinRound Bins { get; } = new();

    public FluidReseedPlan Reseeds { get; } = new();

    public FluidReconcile Reconcile { get; } = new();

    // Complete rounds of the keeper's own bins that went into its ledger or report.
    public long OffersTaken { get; private set; }

    // Cells reseeded because the host wrote them on its own and the keeper's simulation never held them.
    public long ForeignReseeds { get; private set; }

    // The reconcile's cell actions for the GPU, replaced each round that has any; ActionsVersion moves with them.
    public IReadOnlyList<FluidLedgerEntry> Actions => _actions;

    public int ActionsVersion { get; private set; }

    // Per system index, GpuSystemFloats floats: three rows taking a point in the system's frame to the system's own
    // space, then 1 / cell size, cell size, the volume unit the bins add up in (m3), and fill per unit.
    public ReadOnlySpan<float> GpuSystems => _gpuSystems.AsSpan(0, _gpuSystemCount * GpuSystemFloats);

    public int GpuSystemCount => _gpuSystemCount;

    public static FluidLedgerWorld For(World world)
    {
        lock (_all)
        {
            if (!_all.TryGetValue(world, out var space))
                _all[world] = space = new FluidLedgerWorld(world);
            return space;
        }
    }

    public static FluidLedgerWorld? Find(World world)
    {
        lock (_all)
            return world != null && _all.TryGetValue(world, out var space) ? space : null;
    }

    private void Drop(World world)
    {
        lock (_all)
            _all.Remove(world);
        if (Rule != null)
            world.DataModelPermissions?.RemoveRule(Rule);
    }

    public void UseHost(Func<FluidSystem, FluidLedger?> ledgers, Action<FluidSystem, FluidCommand> commands)
    {
        _hostLedgers = ledgers;
        _hostCommands = commands;
        if (Rule != null)
        {
            World.DataModelPermissions?.RemoveRule(Rule);
            Rule = null;
        }
        _ledgers.Clear();
        _writers.Clear();
        _judges.Clear();
        var space = FluidSpace.Find(World);
        if (space != null && !ReferenceEquals(space, _space))
        {
            _space = space;
            space.Packing += PackGpu;
            space.SummaryArrived += TakeSummary;
            space.Destroyed += _ => _space = null;
        }
    }

    public FluidWorldRules? Rules
    {
        get
        {
            double now = World.Time.TotalTime;
            if (now - _rulesAt >= SlowInterval || _rules is { IsDestroyed: true })
            {
                _rules = World.RootSlot?.GetComponent<FluidWorldRules>();
                _rulesAt = now;
            }
            return _rules;
        }
    }

    // SYSTEMS

    public void Track(FluidSystem system)
    {
        if (system == null || system.IsDestroyed || _systems.Contains(system))
            return;
        _systems.Add(system);
        if (!World.IsAuthority)
            return;
        Rule?.Note(system);
        FluidLedger? ledger;
        if (Hosted)
        {
            ledger = _hostLedgers!(system);
        }
        else
        {
            ledger = new FluidLedger(system, Rule);
            _ledgers[system] = ledger;
            _writers[system] = new FluidCommandWriter(system);
        }
        if (ledger != null)
            _judges[system] = new FluidReportJudge(ledger);
        // Made by a guest: its session members land unjudged in the creation, so the host starts them over.
        if (system.ReferenceID.GetUserByte() != RefIDConstants.AUTHORITY_BYTE)
        {
            using (World.DataModelPermissions?.EnterSystemBypass())
            {
                system.Keeper.Target = null!;
                system.Commands.Clear();
                system.CommandSequence.Value = 0;
            }
        }
        ledger?.ResetOrigins();
    }

    public void Untrack(FluidSystem system)
    {
        _systems.Remove(system);
        _ledgers.Remove(system);
        _writers.Remove(system);
        _judges.Remove(system);
        _commandCursors.Remove(system);
        _local.Remove(system);
        Report.Forget(system);
        Rule?.Forget(system);
        Reseeds.Forget(system);
        Reconcile.Forget(system);
        ForgetKeeping(system);
        _shadow.Remove(system);
    }

    // A machine that starts keeping takes the cells as they stand for its own: what its first reports leave out of
    // them is emptied, and none of them is reseeded as written by the host on its own. Not the ones written in the
    // last few seconds: its reconcile may not have laid those in yet, so they stay the host's until it does.
    private void Inherit(FluidSystem system)
    {
        if (!_held.TryGetValue(system, out var held))
            _held[system] = held = new HashSet<(int, int, int, int)>();
        var keys = new List<(int, int, int, int)>();
        double now = World.Time.TotalTime;
        _changedAt.TryGetValue(system, out var changed);
        var cells = system.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                continue;
            if (changed == null || i >= changed.Length || now - changed[i] >= InheritAfter)
                keys.Add(FluidCell.Key(cell));
        }
        foreach (var key in keys)
            held.Add(key);
        Report.Hold(system, keys);
    }

    private void NoteChanged(FluidSystem system, int index, double now)
    {
        if (!_changedAt.TryGetValue(system, out var times) || times.Length <= index)
        {
            var grown = new double[Math.Max(index + 1, system.Cells.Count)];
            Array.Fill(grown, double.NegativeInfinity);
            times?.CopyTo(grown, 0);
            _changedAt[system] = times = grown;
        }
        times[index] = now;
    }

    private void ForgetKeeping(FluidSystem system)
    {
        _inherited.Remove(system);
        _held.Remove(system);
        _keptRounds.Remove(system);
        _foreignDone.Remove(system);
    }

    public void TrackEmitter(FluidEmitter emitter)
    {
        if (emitter != null && !_emitters.Contains(emitter))
            _emitters.Add(emitter);
    }

    public void TrackSensor(FluidSensor sensor)
    {
        if (sensor != null && !_sensors.Contains(sensor))
            _sensors.Add(sensor);
    }

    public FluidLedger? LedgerOf(FluidSystem system)
    {
        if (system == null)
            return null;
        if (Hosted)
            return _hostLedgers!(system);
        return _ledgers.TryGetValue(system, out var l) ? l : null;
    }

    public FluidCommandWriter? WriterOf(FluidSystem system) => system != null && _writers.TryGetValue(system, out var w) ? w : null;

    public FluidReportJudge? JudgeOf(FluidSystem system)
    {
        if (system == null || !World.IsAuthority)
            return null;
        if (_judges.TryGetValue(system, out var judge))
            return judge;
        if (!_systems.Contains(system) || LedgerOf(system) is not { } ledger)
            return null;
        return _judges[system] = new FluidReportJudge(ledger);
    }

    public bool Keeps(FluidSystem system) => World.LocalUser is { } me && ReferenceEquals(system.Keeper.Target, me);

    // Host: a spawn volume fills its system's cells once.
    public double Spawn(FluidSpawnVolume volume)
    {
        if (!World.IsAuthority || volume?.System.Target is not { } system)
            return 0;
        Track(system);
        return LedgerOf(system) is { } ledger ? FluidSpawnFill.Fill(volume, ledger) : 0;
    }

    // Host: a command every GPU machine applies once (a Loom emit, a clear, a settle). Emits and removes it makes
    // count toward the keeper's account.
    public void Command(FluidSystem system, in FluidCommand command)
    {
        if (system == null || !World.IsAuthority)
            return;
        if (Hosted)
            _hostCommands!(system, command);
        else if (WriterOf(system) is { } writer)
            writer.Add(command);
        else
            return;
        if (JudgeOf(system) is { } judge)
            judge.Known(Expected(system, command), World.Time.TotalTime);
    }

    // What a command will do to a system's volume, from the cells as they stand: the keeper's report of it should show
    // about this much, and the judge expects it.
    public double Expected(FluidSystem system, in FluidCommand command)
    {
        if (command.Kind is FluidCommandKind.Emit or FluidCommandKind.Burst)
            return command.Litres;
        if (LedgerOf(system) is not { } ledger)
            return 0;
        float size = ledger.CellSize;
        float r2 = command.Radius * command.Radius;
        double total = 0;
        foreach (var cell in system.Cells)
        {
            if (FluidCell.Fill(cell) == 0)
                continue;
            int origin = ledger.OriginOf(cell);
            bool match = command.Kind switch
            {
                FluidCommandKind.ClearAll => true,
                FluidCommandKind.Remove => (command.AnyOriginMatches || origin == command.Origin)
                    && FluidCell.Anchor(cell) == FluidCell.NoAnchor
                    && float3.DistanceSquared(FluidCell.Centre(cell, size), command.Point) <= r2,
                FluidCommandKind.ClearOrigin or FluidCommandKind.FadeOrigin or FluidCommandKind.Evaporate => origin == command.Origin,
                _ => false,
            };
            if (match)
                total += FluidCell.Litres(cell, size);
        }
        // The ledger lags the keeper: fluid still on its way into the cells (the judge's open balance) may belong to what
        // the command takes. An evaporation asks for an amount the shares computed, and takes that.
        if (command.Kind == FluidCommandKind.Evaporate)
            return -command.Litres;
        total += Math.Max(0, JudgeOf(system)?.Credit ?? 0);
        if (command.Kind == FluidCommandKind.Remove && command.Litres > 0)
            total = Math.Min(total, command.Litres);
        return -total;
    }

    // KEEPER INPUT

    // A complete set of this machine's bins for a system. Used only while this machine keeps it. The space hands over
    // each round's cells as the round completes; the same summary then reaches this coordinator whole (TakeSummary),
    // with what each share holds past a whole cell, so the offer is taken from there. A round this coordinator did not
    // see whole goes in with the cells it was given, at the next update.
    public void OfferBins(FluidSystem system, ReadOnlySpan<int4> cells)
    {
        if (!Keeps(system))
            return;
        _offered[system] = cells.ToArray();
    }

    private void TakeOffers(FluidSpace? space, bool fromRound)
    {
        // A whole round covers every system this machine keeps, the ones it found nothing for included (the space
        // hands over only rounds with bins in them): a kept system with no share in it is empty here.
        if (fromRound && space != null)
            foreach (var system in space.Systems)
                if (!system.IsDestroyed && Keeps(system) && !_offered.ContainsKey(system))
                    _offered[system] = Array.Empty<int4>();
        if (_offered.Count == 0)
            return;
        double now = World.Time.TotalTime;
        foreach (var (system, cells) in _offered)
        {
            // While the whole system is still being laid in (a fresh device world, a new tier), a round is only part of
            // it: offering it would empty every cell not reached yet. Single cells being laid in are safe to offer
            // around: a cell the keeper never held is never reported empty, and the host holds what it wrote itself.
            if (system.IsDestroyed || !Keeps(system) || Reseeds.Rebuilding(system, now))
                continue;
            _merged.Clear();
            _originBins.Clear();
            float size = FluidLedger.CellSizeOf(system);
            void Share(int4 cell, int fill)
            {
                FluidBinRound.Add(_merged, cell, fill);
                int origin = FluidCell.Origin(cell);
                _originBins[origin] = _originBins.GetValueOrDefault(origin) + fill / (double)FluidCell.FillMax * FluidCell.CellLitres(size);
            }
            int index = space?.SystemIndex(system) ?? -1;
            if (fromRound && index >= 0)
            {
                foreach (var bin in Bins.Entries)
                    if (bin.System == (uint)index)
                        Share(bin.Cell, FluidBinRound.FullFillOf(bin));
            }
            else
            {
                foreach (var cell in cells)
                    Share(cell, FluidCell.Fill(cell));
            }
            FluidBinRound.Stack(_merged);
            Offer(system, FluidBinRound.Cells(_merged));
            Report.OfferOrigins(system, _originBins);
        }
        _offered.Clear();
    }

    private void Offer(FluidSystem system, int4[] merged)
    {
        if (!_held.TryGetValue(system, out var held))
            _held[system] = held = new HashSet<(int, int, int, int)>();
        foreach (var cell in merged)
            if (FluidCell.Fill(cell) > 0)
                held.Add(FluidCell.Key(cell));
        _keptRounds[system] = _keptRounds.GetValueOrDefault(system) + 1;
        OffersTaken++;
        Report.Offer(system, merged);
    }

    // Keeper: the cells of a system it keeps that the host wrote on its own (a spawn fill, a load) and that its own
    // simulation has not held since it began keeping, by ledger index. Empty until its first complete round. These are
    // what it reseeds before its next bin; a cell its simulation emptied is never among them.
    public int ForeignCells(FluidSystem system, List<int> output)
    {
        int before = output.Count;
        if (system == null || !Keeps(system) || _keptRounds.GetValueOrDefault(system) == 0)
            return 0;
        var held = _held.GetValueOrDefault(system);
        var cells = system.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor || (FluidCell.Flags(cell) & FluidCell.FlagFading) != 0)
                continue;
            if (held == null || !held.Contains(FluidCell.Key(cell)))
                output.Add(i);
        }
        return output.Count - before;
    }

    public void OfferEvent(FluidSystem system, FluidReportKind kind, int4 payload)
    {
        if (!Keeps(system))
            return;
        if (World.IsAuthority)
            ApplyEvent(system, new FluidReportEntry(system.ReferenceID.RawValue, kind, 0, 1, payload));
        else
            Report.AddEvent(system, kind, payload);
    }

    // Every machine: this system's commands since the last take, oldest first, then the keeper's own removals.
    public int TakeCommands(FluidSystem system, List<FluidCommand> output)
    {
        int before = output.Count;
        int cursor = _commandCursors.GetValueOrDefault(system, FluidRings.NewCursor);
        RingGaps += FluidCommandWriter.Read(system, ref cursor, output);
        _commandCursors[system] = cursor;
        if (_local.TryGetValue(system, out var local))
        {
            output.AddRange(local);
            local.Clear();
        }
        for (int i = before; i < output.Count; i++)
        {
            if (output[i].Kind is not (FluidCommandKind.Evaporate or FluidCommandKind.FadeOrigin))
                continue;
            if (!_handed.TryGetValue(system, out var handed))
                _handed[system] = handed = new Queue<FluidCommand>();
            handed.Enqueue(output[i]);
        }
        return output.Count - before;
    }

    // UPDATE

    public void Update()
    {
        double now = World.Time.TotalTime;
        double dt = double.IsNaN(_lastUpdate) ? 0 : Math.Max(0, now - _lastUpdate);
        _lastUpdate = now;
        _systems.RemoveAll(s => s == null || s.IsDestroyed);
        _emitters.RemoveAll(e => e == null || e.IsDestroyed);
        _sensors.RemoveAll(s => s == null || s.IsDestroyed);
        var rules = Rules;
        GatherPeople();

        TakeOffers(_space, fromRound: false);
        if (World.IsAuthority)
            HostUpdate(now, dt, rules);
        KeeperUpdate(now, rules);
        Pickups.Expire(now);
    }

    private void GatherPeople()
    {
        _users.Clear();
        World.GetAllUsers(_users);
        _people.Clear();
        foreach (var user in _users)
            if (user.Root?.Slot is { } slot)
                _people.Add(slot.GlobalPosition);
    }

    private void HostUpdate(double now, double dt, FluidWorldRules? rules)
    {
        foreach (var system in _systems)
            if (!_judges.ContainsKey(system))
                JudgeOf(system);

        foreach (var emitter in _emitters)
        {
            if (emitter.Flowing.Value && emitter.System.Target is { } target && JudgeOf(target) is { } judge)
                judge.Known(emitter.Rate.Value * dt, now);
        }

        Election.Update(now, _systems, rules);

        foreach (var user in _users)
        {
            var person = FluidProvenance.Of(user);
            if (person == null)
                continue;
            ReadRequests(user, person, now, rules);
            ReadPours(user, person, now, dt, rules);
            ReadReleases(user, person, now, rules);
            Pickups.Settle(user, person);
            ReadReport(user, person, now, rules);
        }

        Fairness.Update(now, rules, Ledgers(), (system, command) => Command(system, command));
        foreach (var judge in _judges.Values)
        {
            judge.Decay(now);
            if (judge.Ledger.AccountDue(now))
                judge.Ledger.PublishAccount(judge.ShouldHold, now);
        }

        if (Hosted)
            return;
        if (now - _lastPass >= PassInterval)
        {
            _lastPass = now;
            int bytes = rules?.LedgerBytesPerSecond.Value ?? 4096;
            foreach (var system in _systems)
                if (_ledgers.TryGetValue(system, out var ledger))
                    ledger.Pass(now, bytes, _people);
            Sensors();
        }
        foreach (var writer in _writers.Values)
            writer.Flush();

        if (now - _lastSlow >= SlowInterval)
        {
            _lastSlow = now;
            using (World.DataModelPermissions?.EnterSystemBypass())
            {
                foreach (var (system, ledger) in _ledgers)
                {
                    float litres = (float)ledger.TotalLitres();
                    if (Math.Abs(system.Litres.Value - litres) > 0.001f)
                        system.Litres.Value = litres;
                }
            }
        }
    }

    private List<FluidLedger> Ledgers()
    {
        _ledgerList.Clear();
        foreach (var system in _systems)
            if (LedgerOf(system) is { } ledger)
                _ledgerList.Add(ledger);
        return _ledgerList;
    }

    private void Sensors()
    {
        if (_sensors.Count == 0)
            return;
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            foreach (var sensor in _sensors)
            {
                double litres = 0;
                foreach (var (system, ledger) in _ledgers)
                    if (sensor.System.Target == null || ReferenceEquals(sensor.System.Target, system))
                        litres += ledger.LitresIn(sensor.Slot, sensor.Shape.Value, sensor.Size.Value);
                if (Math.Abs(sensor.Litres.Value - litres) > 0.0005)
                    sensor.Litres.Value = (float)litres;
                bool wet = litres >= sensor.Threshold.Value;
                if (sensor.Wet.Value != wet)
                    sensor.Wet.Value = wet;
            }
        }
    }

    private bool Next(FluidPerson person, int ring, SyncArray<int4> array, int sequence, List<(int4 A, int4 B)> output)
    {
        output.Clear();
        int cursor = _cursors.GetValueOrDefault((person, ring), FluidRings.NewCursor);
        FluidRings.Read(array, sequence, ref cursor, output);
        _cursors[(person, ring)] = cursor;
        return output.Count > 0;
    }

    private void ReadRequests(User user, FluidPerson person, double now, FluidWorldRules? rules)
    {
        if (!Next(person, RingRequests, person.Requests, person.RequestSequence.Value, _raw))
            return;
        foreach (var (a, b) in _raw)
        {
            if (!Requests.Take(user, now))
                continue;
            var request = FluidRequest.Read(a, b);
            var system = World.ReferenceController.GetObjectOrNull(new RefID(request.System)) as FluidSystem;
            if (system != null)
                Track(system);
            _commands.Clear();
            if (!Requests.Judge(user, request, system, system != null ? LedgerOf(system) : null, rules, _commands))
                continue;
            foreach (var command in _commands)
                Command(system!, command);
        }
    }

    // Pours ride on the holder's own record. A pour counts when its holder may pour and really holds the tool.
    private void ReadPours(User user, FluidPerson person, double now, double dt, FluidWorldRules? rules)
    {
        for (int hand = 0; hand * 2 + 1 < person.Pour.Count; hand++)
        {
            var pour = FluidPour.Read(person.Pour[hand * 2], person.Pour[hand * 2 + 1]);
            if (!pour.IsPouring)
                continue;
            if (World.ReferenceController.GetObjectOrNull(new RefID(pour.Tool)) is not FluidTool tool)
                continue;
            if (!ReferenceEquals(tool.Slot?.ActiveUser, user))
                continue;
            var system = tool.Target.Target;
            if (system == null || LedgerOf(system) is not { } ledger)
                continue;
            if (!FluidRequestJudge.MayPour(World, user, rules, system))
                continue;
            if (_pourSeen.GetValueOrDefault((user, hand), -1) != pour.Number)
            {
                _pourSeen[(user, hand)] = pour.Number;
                ledger.OriginFor(user.ReferenceID.RawValue, pour.Number);
            }
            float rate = Math.Min(pour.Flow, rules?.MaxPourRate.Value ?? 2f);
            JudgeOf(system)?.Known(rate * dt, now);
        }
    }

    private void ReadReleases(User user, FluidPerson person, double now, FluidWorldRules? rules)
    {
        if (!Next(person, RingReleases, person.Releases, person.ReleaseSequence.Value, _raw))
            return;
        float coatLimit = rules?.CoatLimit.Value ?? 200f;
        foreach (var (a, b) in _raw)
        {
            var release = FluidRelease.Read(a, b);
            var system = SystemOf(person, release.System);
            if (system == null || JudgeOf(system) is not { } judge)
                continue;
            if (release.Kind == FluidReleaseKind.Pickup)
            {
                double taken = Pickups.Accept(user, release, system, coatLimit, now, out _);
                if (taken <= 0)
                    continue;
                judge.Known(-taken, now);
                if (Keeps(system))
                    RemovePickup(user, system, release.Cell, taken);
                continue;
            }
            double litres = Math.Max(0, release.Litres);
            if (release.Kind != FluidReleaseKind.Blob)
                litres = Pickups.ClampRelease(user, person, litres);
            else if (!FluidRequestJudge.MayPour(World, user, rules, system))
                litres = 0;
            if (litres > 0)
                judge.Known(litres, now);
        }
    }

    // A keeper's own bake already removed its particles. Remote proposals name exact particles; only a scoop or
    // another unclaimed share falls back to removing fluid from the ledger cell. -xlinka
    private void RemovePickup(User wearer, FluidSystem system, int cell, double litres)
    {
        if (cell == FluidRelease.NoCell)
            return;
        double left = litres - FluidCoatRelay.Claim(World, wearer, system, cell, litres);
        if (left > 1e-9)
            LocalCommands(system).Add(FluidPickupBook.Removal(system, cell, left));
    }

    // What a person last published as on them, in litres: spots at birth plus held goo. Drying only lowers it, so the
    // clamp errs toward letting an honest release through.
    public static double CoatOf(FluidPerson person)
    {
        double litres = 0;
        foreach (var spot in person.Spots)
            litres += Math.Max(0, spot.w) * 1e-3;
        foreach (var held in person.Held)
            litres += Math.Max(0, held.x) * 1e-6;
        return litres;
    }

    // The system a person's entry names through their Touched list.
    public FluidSystem? SystemOf(FluidPerson person, int touched)
    {
        if (touched < 0 || touched >= person.Touched.Count)
            return null;
        var packed = person.Touched[touched];
        var id = FluidRings.Join(packed.x, packed.y);
        return id == 0 ? null : World.ReferenceController.GetObjectOrNull(new RefID(id)) as FluidSystem;
    }

    private void ReadReport(User user, FluidPerson person, double now, FluidWorldRules? rules)
    {
        if (!Next(person, RingReport, person.Report, person.ReportSequence.Value, _raw))
            return;
        bool keeps = false;
        foreach (var system in _systems)
            keeps |= ReferenceEquals(system.Keeper.Target, user);
        if (!keeps || ReferenceEquals(user, World.LocalUser))
        {
            ForeignReportsIgnored += _raw.Count;
            return;
        }
        var incoming = _incoming;
        incoming.Clear();
        foreach (var (a, b) in _raw)
            incoming.Add(FluidReportEntry.Read(a, b));
        if (!_partial.TryGetValue(user, out var partial))
            _partial[user] = partial = new Dictionary<(ulong, int), List<FluidReportEntry>>();
        var complete = _complete;
        complete.Clear();
        FluidKeeperReport.Assemble(incoming, partial, complete);
        int bytes = rules?.LedgerBytesPerSecond.Value ?? 4096;
        var events = _events;
        foreach (var report in complete)
        {
            var system = World.ReferenceController.GetObjectOrNull(new RefID(report[0].System)) as FluidSystem;
            if (system == null || JudgeOf(system) is not { } judge)
            {
                ReportsDropped++;
                Election.NoteFailure(user, now);
                continue;
            }
            double drained = 0;
            foreach (var e in report)
                if (e.Kind == FluidReportKind.Drained)
                    drained += Math.Max(0, e.Payload.z) * 1e-6;
            judge.Known(-drained, now);
            events.Clear();
            if (!judge.Judge(user, report, now, bytes, events))
            {
                judge.Known(drained, now);
                ReportsDropped++;
                Election.NoteFailure(user, now);
                continue;
            }
            foreach (var e in events)
                ApplyEvent(system, e);
        }
    }

    private void ApplyEvent(FluidSystem system, FluidReportEntry entry)
    {
        switch (entry.Kind)
        {
            case FluidReportKind.OriginLitres:
                LedgerOf(system)?.PublishOrigin(entry.Payload.x, entry.Payload.y * 0.001);
                break;
            case FluidReportKind.Drained:
                if (World.ReferenceController.GetObjectOrNull(new RefID(FluidRings.Join(entry.Payload.x, entry.Payload.y))) is FluidDrain drain)
                {
                    using (World.DataModelPermissions?.EnterSystemBypass())
                        drain.Drained.Value += Math.Max(0, entry.Payload.z) * 1e-6f;
                }
                break;
            case FluidReportKind.PropTouch:
            case FluidReportKind.PropSnap:
                PropEvent?.Invoke(system, entry);
                break;
        }
    }

    private List<FluidCommand> LocalCommands(FluidSystem system)
    {
        if (!_local.TryGetValue(system, out var list))
            _local[system] = list = new List<FluidCommand>();
        return list;
    }

    // The keeper's side: its report when it is not the host, and its own share of everyone's Pickups.
    private void KeeperUpdate(double now, FluidWorldRules? rules)
    {
        var me = World.LocalUser;
        if (me == null)
            return;
        var kept = _kept;
        kept.Clear();
        foreach (var system in _systems)
        {
            if (ReferenceEquals(system.Keeper.Target, me))
            {
                kept.Add(system);
                if (_inherited.Add(system))
                    Inherit(system);
            }
            else
            {
                Report.Forget(system);
                if (_keptRounds.ContainsKey(system) || _inherited.Contains(system))
                    ForgetKeeping(system);
            }
        }
        // The host keeping a system itself proposes its cells straight to its ledger, within the account.
        if (World.IsAuthority && kept.Count > 0)
            Report.Propose(now, kept, _people, LedgerOf, JudgeOf);
        bool keeping = kept.Count > 0 && !World.IsAuthority;
        // A machine that starts keeping reads Pickups from now on: entries from before were some other keeper's.
        if (keeping && !_keeping)
        {
            foreach (var key in new List<(FluidPerson, int)>(_cursors.Keys))
                if (key.Item2 == RingReleases)
                    _cursors.Remove(key);
        }
        _keeping = keeping;
        if (!keeping)
            return;

        float coatLimit = rules?.CoatLimit.Value ?? 200f;
        foreach (var user in _users)
        {
            var person = FluidProvenance.Of(user);
            if (person == null || !Next(person, RingReleases, person.Releases, person.ReleaseSequence.Value, _raw))
                continue;
            foreach (var (a, b) in _raw)
            {
                var release = FluidRelease.Read(a, b);
                if (release.Kind != FluidReleaseKind.Pickup || SystemOf(person, release.System) is not { } system || !kept.Contains(system))
                    continue;
                double taken = Pickups.Accept(user, release, system, coatLimit, now, out _);
                if (taken > 0)
                    RemovePickup(user, system, release.Cell, taken);
            }
        }

        var mine = FluidProvenance.Of(me);
        if (mine != null)
            Report.Write(now, mine, kept, rules?.LedgerBytesPerSecond.Value ?? 4096, _people, Proposals);
    }

    // THE GPU SIDE, on a machine that simulates, inside the space's update (summaries) and late update (packing).

    // Each round of this machine's own bins: the reconcile for every system it does not keep. The keeper's share of the
    // same round reaches OfferBins through the space.
    private void TakeSummary(FluidSpace space, FluidSummaryView summary)
    {
        if (!Bins.Take(summary))
            return;
        TakeOffers(space, fromRound: true);
        _roundActions.Clear();
        Reconcile.Round(space, Bins.Entries, World.Time.TotalTime, Reseeds, _roundActions, _wakes, this);
        // A system whose total here stands clearly off (the ledger's, or for the keeper the account) keeps the block
        // stepping for its reconcile, MaxOffRounds rounds at most in a row: past that, what is left is fluid it may not
        // touch (moving, near someone), and the block may sleep.
        if (Reconcile.LastOff > 0 && _offRounds < MaxOffRounds)
        {
            _offRounds++;
            _wakeUntilRound = Math.Max(_wakeUntilRound, Bins.Rounds + 1);
        }
        else if (Reconcile.LastOff == 0)
        {
            _offRounds = 0;
        }
        if (_roundActions.Count > 0)
        {
            _actions.Clear();
            _actions.AddRange(_roundActions);
            ActionsVersion++;
        }
    }

    // After the space packed its block: the reseeds this frame asks for, the keeper's reseed of what the host wrote on
    // its own, the ledger changes since the last frame (they keep a sleeping block stepping until the reconcile has
    // seen them), the reconcile's wake boxes, and each system's space for the bin kernel.
    private void PackGpu(FluidSpace space, FluidFrameBlock block)
    {
        if (!space.Simulating)
            return;
        double now = World.Time.TotalTime;
        var head = World.LocalUser?.Root?.HeadPosition ?? float3.Zero;
        var systems = space.Systems;

        // The packer's whole-system reseed (a fresh device world, a new layout or tier, an epoch) is a Reseed with no
        // particles; this lays the system's cells in.
        int packed = block.Commands.Count;
        for (int c = 0; c < packed; c++)
        {
            var command = block.Commands[c];
            if (command.Op != (uint)FluidGpuOp.Reseed || command.Count != 0 || command.A.w > 0f)
                continue;
            if (space.SystemAt((int)command.System) is { IsDestroyed: false } whole)
            {
                Reseeds.All(whole, head, now);
                Reconcile.Forget(whole);
                _foreignDone.Remove(whole);
            }
        }

        foreach (var system in systems)
        {
            if (system.IsDestroyed || !Keeps(system))
                continue;
            _foreign.Clear();
            if (ForeignCells(system, _foreign) == 0)
                continue;
            if (!_foreignDone.TryGetValue(system, out var done))
                _foreignDone[system] = done = new Dictionary<(int, int, int, int), int4>();
            // Only what this simulation lacks in each cell, and only while that keeps its total within the account:
            // water a reseed lays over water already here, or the account already covers elsewhere, is fluid made
            // from nothing. A cell held back is tried again next frame.
            float size = FluidLedger.CellSizeOf(system);
            bool hasAccount = AccountOf(system, out double account);
            double holds = Report.BinsLitres(system) + Reseeds.QueuedLitres(system);
            double over = Math.Max(FluidReconcile.SystemTolerance * account, FluidCell.CellLitres(size));
            foreach (int i in _foreign)
            {
                var cell = system.Cells[i];
                var key = FluidCell.Key(cell);
                // Once per value: a cell that does not show up in the bins after its reseed is not laid in twice.
                if (done.TryGetValue(key, out var was) && was == cell)
                    continue;
                if (Reseeded(block, space.SystemIndex(system), cell, size, space))
                    continue;
                double deficit = FluidCell.Litres(cell, size) - Report.BinLitres(system, key);
                if (deficit <= FluidReconcile.Disagreement * FluidCell.CellLitres(size))
                {
                    done[key] = cell;
                    continue;
                }
                if (hasAccount && holds + deficit > account + over)
                {
                    ForeignHeld++;
                    continue;
                }
                done[key] = cell;
                holds += deficit;
                Reseeds.Cell(system, cell, deficit, head, now);
                ForeignReseeds++;
            }
        }

        bool changed = false;
        int written = 0;
        for (int index = 0; index < systems.Count; index++)
        {
            var system = systems[index];
            if (system.IsDestroyed)
                continue;
            var cells = system.Cells;
            if (!_shadow.TryGetValue(system, out var shadow) || shadow.Length != cells.Count)
            {
                var fresh = new int4[cells.Count];
                for (int i = 0; i < fresh.Length; i++)
                {
                    fresh[i] = cells[i];
                    if (shadow == null || i >= shadow.Length || shadow[i] != fresh[i])
                        written = Change(block, index, i, fresh[i], system, written, ref changed);
                }
                _shadow[system] = fresh;
                continue;
            }
            for (int i = 0; i < shadow.Length; i++)
            {
                var cell = cells[i];
                if (cell == shadow[i])
                    continue;
                shadow[i] = cell;
                written = Change(block, index, i, cell, system, written, ref changed);
            }
        }
        if (changed)
            _wakeUntilRound = Bins.Rounds + ReconcileRoundsAfterChange;
        // The packer decided the block sleeps before this ran; ledger changes, or a reconcile still owed rounds, keep it
        // stepping (a sleeping block records nothing, bins included).
        if (block.LedgerChanges.Count > 0 || Bins.Rounds < _wakeUntilRound)
            block.Asleep = false;

        Restore(space, block);
        if (_wakes.Count > 0)
        {
            block.Commands.AddRange(_wakes);
            _wakes.Clear();
        }
        Reseeds.Pack(space, block, now);
        PackSystems(space, block);
    }

    // Evaporate carries litres in A.x (or a share in A.y) and FadeOrigin its seconds in A.x, with no point; the packer
    // converts every command's A.xyz from the system's space to the frame's, which turns those into a position (an
    // evaporation then takes whatever share the frame's y happens to be). They are put back here, in the order they
    // were handed out, until the packer converts points only for the commands that have one.
    private void Restore(FluidSpace space, FluidFrameBlock block)
    {
        if (_handed.Count == 0)
            return;
        for (int c = 0; c < block.Commands.Count; c++)
        {
            var command = block.Commands[c];
            if (command.Op != (uint)FluidGpuOp.Evaporate && command.Op != (uint)FluidGpuOp.FadeOrigin)
                continue;
            if (space.SystemAt((int)command.System) is not { } system || !_handed.TryGetValue(system, out var handed) || handed.Count == 0)
                continue;
            var gpu = handed.Dequeue().ToGpu(command.System);
            if (gpu.Op != command.Op)
                continue;
            command.A = gpu.A;
            block.Commands[c] = command;
            CommandsRestored++;
        }
        _handed.Clear();
    }

    public int CommandsRestored { get; private set; }

    // The litres a system should hold: the host's own account, or the one it published.
    public bool AccountOf(FluidSystem system, out double account)
    {
        account = 0;
        if (!World.IsAuthority)
            return FluidLedger.TryAccount(system, out account);
        if (JudgeOf(system) is not { } judge)
            return false;
        account = judge.ShouldHold;
        return true;
    }

    public int ForeignHeld { get; private set; }

    // A system this machine keeps: the litres the host's account says it should hold, and how far its own simulation
    // may stand from that before its reconcile acts (3% or a cell, plus the account's own movement over the last
    // AccountWindow seconds, which is fluid on its way). False with no account.
    public bool KeeperAccount(FluidSystem system, out double account, out double tolerance)
    {
        tolerance = 0;
        if (!AccountOf(system, out account))
            return false;
        double now = World.Time.TotalTime;
        if (!_accounts.TryGetValue(system, out var seen))
            _accounts[system] = seen = new Queue<(double, double)>();
        seen.Enqueue((now, account));
        while (seen.Count > 0 && now - seen.Peek().At > AccountWindow)
            seen.Dequeue();
        double low = account, high = account;
        foreach (var (_, litres) in seen)
        {
            low = Math.Min(low, litres);
            high = Math.Max(high, litres);
        }
        float size = FluidLedger.CellSizeOf(system);
        tolerance = Math.Max(FluidReconcile.SystemTolerance * account, FluidCell.CellLitres(size)) + (high - low);
        return true;
    }

    private int Change(FluidFrameBlock block, int index, int i, int4 cell, FluidSystem system, int written, ref bool changed)
    {
        NoteChanged(system, i, World.Time.TotalTime);
        if (!system.IsKeeperHere)
            changed = true;
        if (written >= MaxLedgerChanges)
            return written;
        int tint = i < system.CellTint.Count ? system.CellTint[i] : 0;
        block.LedgerChanges.Add(new FluidLedgerEntry { System = (uint)index, Index = (uint)i, Tint = (uint)tint, Cell = cell });
        return written + 1;
    }

    // Someone else in this block already reseeds that cell (a lane that lays the keeper's cells in itself).
    private static bool Reseeded(FluidFrameBlock block, int index, int4 cell, float size, FluidSpace space)
    {
        if (index < 0)
            return false;
        foreach (var c in block.Commands)
        {
            if (c.Op != (uint)FluidGpuOp.Reseed || c.Count == 0 || c.System != (uint)index)
                continue;
            var system = space.SystemAt(index);
            if (system?.Slot == null)
                return false;
            var global = system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
            var point = space.Packer.PointToFrame(space.Packer.FrameOf(index), global);
            if (float3.DistanceSquared(new float3(c.A.x, c.A.y, c.A.z), point) < 0.25f * size * size)
                return true;
        }
        return false;
    }

    // Each system's frame space to its own space, and the units its bins add up in.
    private void PackSystems(FluidSpace space, FluidFrameBlock block)
    {
        var systems = space.Systems;
        int count = Math.Min(systems.Count, FluidSpace.MaxSystems);
        for (int index = 0; index < count; index++)
        {
            var system = systems[index];
            int at = index * GpuSystemFloats;
            if (system.IsDestroyed || system.Slot is not { IsDestroyed: false } slot)
            {
                Array.Clear(_gpuSystems, at, GpuSystemFloats);
                continue;
            }
            int frame = space.Packer.FrameOf(index);
            var f = frame > 0 && frame < block.Frames.Count ? block.Frames[frame] : new FluidFrameRecord { Rotation = floatQ.Identity, Scale = 1f };
            float3 FrameToSystem(float3 p) => slot.GlobalPointToLocal(f.Position + f.Rotation * (p * f.Scale));
            var o = FrameToSystem(float3.Zero);
            var ex = FrameToSystem(new float3(1f, 0f, 0f)) - o;
            var ey = FrameToSystem(new float3(0f, 1f, 0f)) - o;
            var ez = FrameToSystem(new float3(0f, 0f, 1f)) - o;
            float size = FluidLedger.CellSizeOf(system);
            float scale = MathF.Abs(slot.GlobalScale.x);
            double cellVolume = Math.Pow(size * Math.Max(scale, 1e-6f), 3);
            // A cell adds up to about a billion units at most, well inside the kernel's 32 bits.
            double unit = Math.Max(1e-15, cellVolume / 1e9);
            _gpuSystems[at + 0] = ex.x; _gpuSystems[at + 1] = ey.x; _gpuSystems[at + 2] = ez.x; _gpuSystems[at + 3] = o.x;
            _gpuSystems[at + 4] = ex.y; _gpuSystems[at + 5] = ey.y; _gpuSystems[at + 6] = ez.y; _gpuSystems[at + 7] = o.y;
            _gpuSystems[at + 8] = ex.z; _gpuSystems[at + 9] = ey.z; _gpuSystems[at + 10] = ez.z; _gpuSystems[at + 11] = o.z;
            _gpuSystems[at + 12] = 1f / size;
            _gpuSystems[at + 13] = size;
            _gpuSystems[at + 14] = (float)unit;
            _gpuSystems[at + 15] = (float)(FluidCell.FillMax * unit / cellVolume);
        }
        _gpuSystemCount = count;
    }

    private void OnUserRemoved(User user)
    {
        if (user == null)
            return;
        Fairness.UserLeft(user.ReferenceID.RawValue, Rules, Ledgers(), (system, command) => Command(system, command));
        Requests.Forget(user);
        _partial.Remove(user);
        Pickups.Forget(user);
        _pourSeen.Remove((user, 0));
        _pourSeen.Remove((user, 1));
    }
}
