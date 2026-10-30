// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The keeper's report ring writer, on the keeper's own FluidPerson, when the keeper is not the host. At 4 Hz while
// anything is awake it compares its own binned cells with the ledger every peer holds and writes the differences
// that matter: past 2% of a cell settled or 10% Moving, or a changed kind; largest first, nearest people first, within
// half of LedgerBytesPerSecond. Nothing while settled. A new keeper's first report is therefore a full diff of its bins
// against the cells, and it is already small, because every GPU machine reconciles toward the ledger. The host keeping
// a system itself picks its cells the same way and proposes them straight to its ledger.
//
// Entries are absolute cell states, so one the host already wrote changes nothing. An entry stays in flight until the
// ledger cell it was sent against changes (or 10 s, for a report the host dropped), and the keeper reads the ledger as
// it will be once its entries land, so it never sends the same change twice. A ledger cell the keeper's simulation
// never held (the host wrote it on its own, a spawn fill, and the reseed has not reached it yet) is not reported
// empty: only a cell it binned at some point since it began keeping can be emptied by it. Each report fits the ring
// whole (at most 63 entries) and carries its own number and size, so the host can judge it whole. Events (drains,
// container volumes, prop touches) and coat proposals ride in the same reports.
//
// A keeper that takes over inherits the settled cells the last one left (unchanged for 3 s): they count as held, so
// its first reports empty the ones its own simulation does not have, and nothing reseeds them.
//
// The keeper decides where fluid is; it never decides how much there is. The host publishes the system's account (what
// it should hold: the cells plus every known flow no report has shown yet, FluidLedger.PublishAccount), and the
// reports' total change goes toward it: the ledger moves no further from the account than AccountShare of it (or
// AccountFloor), and a ledger already outside that only moves back. Differences that would push it further wait.
// Fluid the keeper's simulation made or lost on its own (a reseed laid over water already there, a fade the solver
// merged back, a copy that stalled) therefore never reaches the ledger, however often the keeper changes; the
// keeper's own reconcile takes its simulation back to the account instead. A pour still reaches the ledger as fast as
// the bandwidth allows, because it is in the account. With no account published (an older host) reports aim at the
// keeper's own bins total within the judge's slack, as before. -xlinka
public sealed class FluidKeeperReport
{
    public const double Interval = 0.25;
    public const double InflightSeconds = 10.0;
    public const int MaxEntries = FluidPerson.ReportCount / 2 - 1;
    // How far the ledger may stand from the account while fluid moves between cells: this share of it, or the floor.
    public const double AccountShare = 0.0025;
    public const double AccountFloor = 0.002;

    private readonly Dictionary<FluidSystem, List<int4>> _bins = new();
    private readonly Dictionary<FluidSystem, Dictionary<int, int>> _origins = new();
    private readonly Dictionary<(FluidSystem, int), (int Ml, double At)> _sentOrigins = new();
    private readonly Dictionary<FluidSystem, Dictionary<(int, int, int, int), int4>> _binMap = new();
    private readonly Dictionary<FluidSystem, HashSet<(int, int, int, int)>> _held = new();
    private readonly Dictionary<FluidSystem, List<FluidReportEntry>> _events = new();
    private readonly Dictionary<(FluidSystem, (int, int, int, int)), (int4 Sent, int4 Was, double At)> _inflight = new();
    private readonly Dictionary<FluidSystem, (Dictionary<(int, int, int, int), int4> Cells, double Litres, double Low, double High)> _view = new();
    private readonly List<(FluidSystem System, int4 Cell, float Score)> _candidates = new();
    private readonly List<(FluidSystem System, int4 Cell)> _chosen = new();
    private readonly Dictionary<FluidSystem, double> _net = new();
    private readonly List<(FluidSystem, (int, int, int, int))> _landed = new();
    // the replicated cells as they stand, per kept system, from the last report
    private readonly Dictionary<FluidSystem, Dictionary<(int, int, int, int), int4>> _raw = new();
    private double _lastWrite = double.NegativeInfinity;
    private double _lastPropose = double.NegativeInfinity;
    private double _tokens;
    private int _number;

    public FluidKeeperReport(World world)
    {
        World = world;
    }

    public World World { get; }

    public int Reports { get; private set; }

    public int EntriesWritten { get; private set; }

    // Differences held for a later report to keep the ledger's total on the account.
    public int Deferred { get; private set; }

    // Litres in entries sent and not landed yet, over every system, as the last report saw them.
    public double InflightLitres { get; private set; }

    // The last report's distance from the ledger (as it will be) to the account, for the last kept system.
    public double LastTarget { get; private set; }

    // A complete set of this machine's bins for a system, replacing the last.
    public void Offer(FluidSystem system, ReadOnlySpan<int4> cells)
    {
        if (!_bins.TryGetValue(system, out var list))
            _bins[system] = list = new List<int4>();
        if (!_binMap.TryGetValue(system, out var map))
            _binMap[system] = map = new Dictionary<(int, int, int, int), int4>();
        if (!_held.TryGetValue(system, out var held))
            _held[system] = held = new HashSet<(int, int, int, int)>();
        list.Clear();
        map.Clear();
        foreach (var c in cells)
        {
            list.Add(c);
            var key = FluidCell.Key(c);
            map[key] = c;
            held.Add(key);
        }
    }

    // Complete per-origin bin totals, before mixed cells choose a display substance and origin. -xlinka
    public void OfferOrigins(FluidSystem system, IReadOnlyDictionary<int, double> litres)
    {
        if (!_origins.TryGetValue(system, out var values))
            _origins[system] = values = new Dictionary<int, int>();
        values.Clear();
        for (int origin = 0; origin < system.Origins.Count; origin++)
            values[origin] = (int)Math.Clamp(Math.Round(litres.GetValueOrDefault(origin) * 1000.0), 0, int.MaxValue);
    }

    public bool HasBins(FluidSystem system) => _bins.ContainsKey(system);

    // What this machine's last bins hold in one cell, and in all.
    public double BinLitres(FluidSystem system, (int, int, int, int) key)
        => _binMap.TryGetValue(system, out var map) && map.TryGetValue(key, out var cell) ? FluidCell.Litres(cell, FluidLedger.CellSizeOf(system)) : 0;

    public double BinsLitres(FluidSystem system)
    {
        double total = 0;
        if (_bins.TryGetValue(system, out var bins))
        {
            float size = FluidLedger.CellSizeOf(system);
            foreach (var c in bins)
                total += FluidCell.Litres(c, size);
        }
        return total;
    }

    // A cell this keeper sent and the ledger has not taken yet.
    public bool InFlight(FluidSystem system, (int, int, int, int) key) => _inflight.ContainsKey((system, key));

    // Cells this keeper answers for although it never binned them: the ones the last keeper left when this one took
    // over.
    public void Hold(FluidSystem system, IEnumerable<(int, int, int, int)> keys)
    {
        if (!_held.TryGetValue(system, out var held))
            _held[system] = held = new HashSet<(int, int, int, int)>();
        foreach (var key in keys)
            held.Add(key);
    }

    public void AddEvent(FluidSystem system, FluidReportKind kind, int4 payload)
    {
        if (!_events.TryGetValue(system, out var list))
            _events[system] = list = new List<FluidReportEntry>();
        list.Add(new FluidReportEntry(system.ReferenceID.RawValue, kind, 0, 0, payload));
    }

    public void Forget(FluidSystem system)
    {
        _bins.Remove(system);
        _origins.Remove(system);
        foreach (var key in new List<(FluidSystem, int)>(_sentOrigins.Keys))
            if (ReferenceEquals(key.Item1, system))
                _sentOrigins.Remove(key);
        _binMap.Remove(system);
        _held.Remove(system);
        _events.Remove(system);
        _view.Remove(system);
        _raw.Remove(system);
        _landed.Clear();
        foreach (var key in _inflight.Keys)
            if (ReferenceEquals(key.Item1, system))
                _landed.Add(key);
        foreach (var key in _landed)
            _inflight.Remove(key);
        _landed.Clear();
    }

    // The differences between bins and the ledger as cells, for every system this machine keeps, against the ledger as
    // it stands.
    public void Diff(FluidSystem system, List<(FluidSystem System, int4 Cell, float Score)> output, IReadOnlyList<float3>? people, double now)
    {
        var ledger = new Dictionary<(int, int, int, int), int4>();
        foreach (var cell in system.Cells)
            if (FluidCell.Fill(cell) > 0)
                ledger[FluidCell.Key(cell)] = cell;
        Diff(system, ledger, output, people);
    }

    private void Diff(FluidSystem system, Dictionary<(int, int, int, int), int4> ledger, List<(FluidSystem System, int4 Cell, float Score)> output,
        IReadOnlyList<float3>? people)
    {
        if (!_bins.TryGetValue(system, out var bins))
            return;
        float size = FluidLedger.CellSizeOf(system);
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var cell in bins)
        {
            var key = FluidCell.Key(cell);
            seen.Add(key);
            Consider(system, cell, ledger.GetValueOrDefault(key), size, output, people);
        }
        var held = _held.GetValueOrDefault(system);
        foreach (var (key, current) in ledger)
        {
            if (!seen.Contains(key) && held != null && held.Contains(key) && FluidCell.Fill(current) > 0)
                Consider(system, FluidCell.WithFill(current, 0), current, size, output, people);
        }
    }

    private static void Consider(FluidSystem system, int4 cell, int4 current, float size, List<(FluidSystem, int4, float)> output, IReadOnlyList<float3>? people)
    {
        bool moving = FluidCell.IsMoving(cell) || FluidCell.IsMoving(current);
        float diff = Math.Abs(FluidCell.Fill(cell) - FluidCell.Fill(current)) / (float)FluidCell.FillMax;
        bool kind = !FluidCell.SameKind(cell, current) && (FluidCell.Fill(cell) > 0 || FluidCell.Fill(current) > 0);
        if (!kind && diff <= (moving ? FluidLedger.MovingThreshold : FluidLedger.SettledThreshold))
            return;
        float distance = 0f;
        if (people != null && people.Count > 0 && system.Slot != null && FluidCell.Anchor(cell) == FluidCell.NoAnchor)
        {
            var centre = system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
            distance = float.MaxValue;
            foreach (var p in people)
                distance = Math.Min(distance, float3.Distance(p, centre));
        }
        output.Add((system, cell, Math.Max(diff, kind ? 1f : 0f) / (1f + distance)));
    }

    // The ledger as this keeper will see it once its entries land: the cells, each one in flight as it was sent.
    // Entries whose cell changed since they were sent have landed (or were overtaken) and stop counting.
    private Dictionary<(int, int, int, int), int4> GuestView(FluidSystem system, double now, out double inflight)
    {
        if (!_raw.TryGetValue(system, out var raw))
            _raw[system] = raw = new Dictionary<(int, int, int, int), int4>();
        raw.Clear();
        foreach (var cell in system.Cells)
            if (FluidCell.Fill(cell) > 0)
                raw[FluidCell.Key(cell)] = cell;
        var cells = new Dictionary<(int, int, int, int), int4>(raw);
        float size = FluidLedger.CellSizeOf(system);
        inflight = 0;
        _landed.Clear();
        foreach (var (id, entry) in _inflight)
        {
            if (!ReferenceEquals(id.Item1, system))
                continue;
            var current = raw.GetValueOrDefault(id.Item2);
            if (current != entry.Was || now - entry.At > InflightSeconds)
            {
                _landed.Add(id);
                continue;
            }
            inflight += FluidCell.Litres(entry.Sent, size) - FluidCell.Litres(entry.Was, size);
            if (FluidCell.Fill(entry.Sent) > 0)
                cells[id.Item2] = entry.Sent;
            else
                cells.Remove(id.Item2);
        }
        foreach (var id in _landed)
            _inflight.Remove(id);
        _landed.Clear();
        return cells;
    }

    // Where the ledger may end up after this report: no further from the account than the band round it, and only
    // back toward it when already outside. Without an account, toward the bins within the judge's slack.
    private (double Low, double High) Band(FluidSystem system, double ledgerLitres, bool hasAccount, double account)
    {
        float size = FluidLedger.CellSizeOf(system);
        if (hasAccount)
        {
            double target = account - ledgerLitres;
            double band = Math.Max(AccountShare * Math.Max(account, 0), AccountFloor);
            LastTarget = target;
            return (Math.Min(0, target - band), Math.Max(0, target + band));
        }
        double toBins = BinsLitres(system) - ledgerLitres;
        double slack = FluidReportJudge.Tolerance * ledgerLitres + FluidCell.CellLitres(size);
        LastTarget = toBins;
        return (Math.Min(0, toBins) - slack, Math.Max(0, toBins) + slack);
    }

    // Picks this round's entries for every kept system into _chosen, at most budget of them, each system's total
    // change kept inside its band; an entry that does not fit now is tried again after each one that does.
    private void Choose(int budget)
    {
        _chosen.Clear();
        _net.Clear();
        _candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        var waiting = new List<int>();
        for (int i = 0; i < _candidates.Count && _chosen.Count < budget; i++)
        {
            if (!Take(i))
            {
                waiting.Add(i);
                continue;
            }
            bool progress = true;
            while (progress && waiting.Count > 0 && _chosen.Count < budget)
            {
                progress = false;
                for (int w = 0; w < waiting.Count; w++)
                {
                    if (Take(waiting[w]))
                    {
                        waiting.RemoveAt(w);
                        progress = true;
                        break;
                    }
                }
            }
        }
        Deferred += waiting.Count;
    }

    private bool Take(int i)
    {
        var (system, cell, _) = _candidates[i];
        if (!_view.TryGetValue(system, out var view))
            return false;
        float size = FluidLedger.CellSizeOf(system);
        double change = FluidCell.Litres(cell, size) - FluidCell.Litres(view.Cells.GetValueOrDefault(FluidCell.Key(cell)), size);
        double net = _net.GetValueOrDefault(system);
        double next = net + change;
        double low = view.Low, high = view.High;
        if ((next < low || next > high) && Math.Abs(next - Math.Clamp(next, low, high)) >= Math.Abs(net - Math.Clamp(net, low, high)))
            return false;
        _net[system] = next;
        _chosen.Add((system, cell));
        return true;
    }

    // Keeper, not host: writes one report into its own ring at 4 Hz. Returns how many entries it wrote.
    public int Write(double now, FluidPerson me, IReadOnlyList<FluidSystem> kept, int ledgerBytesPerSecond, IReadOnlyList<float3>? people,
        FluidCoatProposals? proposals = null)
    {
        if (me == null || me.Report.Count < FluidPerson.ReportCount || now - _lastWrite < Interval)
            return 0;
        // The host holds a keeper to half the ledger's bytes over any one second, plus a quarter. A bucket that may only
        // save up half a report more than its share never bursts past that.
        double rate = Math.Max(256, ledgerBytesPerSecond * 0.5);
        _tokens = double.IsNegativeInfinity(_lastWrite) ? rate * Interval : Math.Min(rate * Interval * 1.5, _tokens + rate * (now - _lastWrite));
        _lastWrite = now;

        var entries = new List<FluidReportEntry>();
        foreach (var system in kept)
        {
            if (_events.TryGetValue(system, out var events))
            {
                entries.AddRange(events);
                events.Clear();
            }
        }
        proposals?.Drain(entries);
        // Origin measurements share the report budget and remain queued until a later round has room. -xlinka
        int originBudget = Math.Min(MaxEntries, (int)Math.Floor(_tokens / FluidReportJudge.EntryBytes));
        originBudget = Math.Min(originBudget, entries.Count + Math.Max(1, originBudget / 3));
        foreach (var system in kept)
        {
            if (!_origins.TryGetValue(system, out var origins))
                continue;
            foreach (var (origin, ml) in origins)
            {
                if (entries.Count >= originBudget)
                    break;
                var published = system.Origins[origin];
                if (origin == 0 ? published.y == FluidLedger.OriginMeasured && published.x == ml
                    : (published.z & FluidLedger.OriginMeasured) != 0 && published.w == ml)
                    continue;
                if (_sentOrigins.TryGetValue((system, origin), out var sent) && sent.Ml == ml && now - sent.At < InflightSeconds)
                    continue;
                entries.Add(new FluidReportEntry(system.ReferenceID.RawValue, FluidReportKind.OriginLitres, 0, 0, new int4(origin, ml, 0, 0)));
                _sentOrigins[(system, origin)] = (ml, now);
            }
        }

        _candidates.Clear();
        _view.Clear();
        double inflightAll = 0;
        foreach (var system in kept)
        {
            var cells = GuestView(system, now, out double inflight);
            inflightAll += inflight;
            float size = FluidLedger.CellSizeOf(system);
            double litres = 0;
            foreach (var (_, c) in cells)
                litres += FluidCell.Litres(c, size);
            bool has = FluidLedger.TryAccount(system, out double account);
            var (low, high) = Band(system, litres, has, account);
            _view[system] = (cells, litres, low, high);
            Diff(system, cells, _candidates, people);
        }
        InflightLitres = inflightAll;
        int budget = (int)Math.Min(MaxEntries, Math.Floor(_tokens / FluidReportJudge.EntryBytes)) - entries.Count;
        Choose(Math.Max(0, budget));
        foreach (var (system, cell) in _chosen)
        {
            var key = FluidCell.Key(cell);
            entries.Add(new FluidReportEntry(system.ReferenceID.RawValue, FluidReportKind.Cell, 0, 0, cell));
            _inflight[(system, key)] = (cell, _raw.TryGetValue(system, out var raw) ? raw.GetValueOrDefault(key) : default, now);
        }
        if (entries.Count == 0)
            return 0;
        if (entries.Count > MaxEntries)
            entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);

        _number++;
        // One report per system, so the host can judge each against its own system's account.
        var bySystem = new Dictionary<ulong, List<FluidReportEntry>>();
        foreach (var e in entries)
        {
            if (!bySystem.TryGetValue(e.System, out var list))
                bySystem[e.System] = list = new List<FluidReportEntry>();
            list.Add(e);
        }
        int written = 0;
        foreach (var (system, list) in bySystem)
        {
            int number = _number;
            foreach (var e in list)
            {
                new FluidReportEntry(system, e.Kind, number, list.Count, e.Payload).Write(out var header, out var payload);
                FluidRings.Write(me.Report, me.ReportSequence, header, payload);
                written++;
            }
            _number++;
        }
        _tokens -= written * FluidReportJudge.EntryBytes;
        Reports += bySystem.Count;
        EntriesWritten += written;
        return written;
    }

    // The host keeping a system itself, at 4 Hz: the same choice against its ledger as it will be once written, no
    // byte budget, each chosen cell proposed straight away and its change drawn from the account. Returns the cells.
    public int Propose(double now, IReadOnlyList<FluidSystem> kept, IReadOnlyList<float3>? people, Func<FluidSystem, FluidLedger?> ledgerOf,
        Func<FluidSystem, FluidReportJudge?> judgeOf)
    {
        if (now - _lastPropose < Interval)
            return 0;
        _lastPropose = now;
        _candidates.Clear();
        _view.Clear();
        foreach (var system in kept)
        {
            if (ledgerOf(system) is not { } ledger)
                continue;
            if (_origins.TryGetValue(system, out var origins))
                foreach (var (origin, ml) in origins)
                    ledger.PublishOrigin(origin, ml * 0.001);
            var cells = new Dictionary<(int, int, int, int), int4>();
            ledger.ExpectedCells(cells);
            float size = FluidLedger.CellSizeOf(system);
            double litres = 0;
            foreach (var (_, c) in cells)
                litres += FluidCell.Litres(c, size);
            var judge = judgeOf(system);
            var (low, high) = Band(system, litres, judge != null, judge?.ShouldHold ?? 0);
            _view[system] = (cells, litres, low, high);
            Diff(system, cells, _candidates, people);
        }
        Choose(int.MaxValue);
        int proposed = 0;
        foreach (var (system, cell) in _chosen)
        {
            if (ledgerOf(system) is not { } ledger)
                continue;
            ledger.Propose(cell);
            proposed++;
        }
        foreach (var (system, net) in _net)
            judgeOf(system)?.Spend(net);
        EntriesWritten += proposed;
        return proposed;
    }

    // Host side: assembles whole reports from the entries read off a keeper's ring. A report is complete once every
    // entry of its number arrived; a number that a newer one overtook is dropped incomplete.
    public static void Assemble(List<FluidReportEntry> incoming, Dictionary<(ulong System, int Number), List<FluidReportEntry>> partial,
        List<List<FluidReportEntry>> complete)
    {
        foreach (var entry in incoming)
        {
            var key = (entry.System, entry.Epoch);
            if (!partial.TryGetValue(key, out var list))
                partial[key] = list = new List<FluidReportEntry>();
            list.Add(entry);
            if (list.Count >= entry.Count)
            {
                complete.Add(list);
                partial.Remove(key);
            }
        }
        if (partial.Count > 8)
        {
            var stale = new List<(ulong, int)>(partial.Keys);
            stale.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            for (int i = 0; i < stale.Count - 8; i++)
                partial.Remove(stale[i]);
        }
    }
}
