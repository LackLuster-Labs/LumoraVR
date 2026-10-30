// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The host's check of a keeper's report, one per system. A report passes when:
//   - its sender is the system's current keeper and every entry is for this system;
//   - every cell lies inside the system's bounds grown by 1 m, or is keyed to one of its anchor slots;
//   - the volume it changes lies within what is known to have happened since, give or take 3% of the system plus one
//     cell. Known additions are flowing emitters, approved emits and bursts, releases off people and the pours and
//     blobs of holders who may pour; known removals are reported drains, accepted Pickups, removes and evaporation.
//     They go into one running account the reports draw on, so a keeper whose bandwidth lets it report a big pour
//     over several reports passes each of them, and a report that makes 2 L from nothing does not;
//   - it stays within half of LedgerBytesPerSecond, plus 25%, over the last second.
// The host and the keeper see a flow start and stop at different moments (their own frames, the replication between),
// so the slack also takes in the last second of what was known to flow, both ways: a pour that ran on a little longer
// in the keeper's simulation than on the host's clock is not an invention.
// A report that fails is dropped and logged once; three failures in 30 s bench the keeper. Nothing is reported as a
// denial: a broken driver is not an attack, and the denial score leads toward a kick.
//
// What a bad keeper can still do is move settled fluid around inside those bounds. The stakes are a puddle's shape.
// Worlds that care set KeeperAccess to Builders. -xlinka
public sealed class FluidReportJudge
{
    public const float Tolerance = 0.03f;
    public const float BoundsMargin = 1f;
    public const double QuietSeconds = 30.0;
    public const int EntryBytes = 2 * FluidLedger.SparseEntryBytes;
    public const double LatencySeconds = 1.0;

    private readonly Queue<(double At, int Bytes)> _sent = new();
    private readonly Queue<(double At, double Litres)> _recent = new();
    private double _recentSum;
    private bool _loggedFailure;

    public FluidReportJudge(FluidLedger ledger)
    {
        Ledger = ledger;
    }

    public FluidLedger Ledger { get; }

    public FluidSystem System => Ledger.System;

    // Litres known to have gone in (positive) or out (negative) that no accepted report has shown yet.
    public double Credit { get; private set; }

    public int Passed { get; private set; }

    // Everything known to have gone in and out, and what accepted reports changed, since this judge began.
    public double KnownIn { get; private set; }

    public double KnownOut { get; private set; }

    public double Reported { get; private set; }

    public int Failed { get; private set; }

    public string LastFailure { get; private set; } = string.Empty;

    public void Known(double litres, double now)
    {
        if (litres == 0 || !double.IsFinite(litres))
            return;
        Credit += litres;
        if (litres > 0)
            KnownIn += litres;
        else
            KnownOut -= litres;
        _recent.Enqueue((now, Math.Abs(litres)));
        _recentSum += Math.Abs(litres);
    }

    // Litres known to have moved, either way, over the last LatencySeconds.
    public double Recent(double now)
    {
        while (_recent.Count > 0 && now - _recent.Peek().At > LatencySeconds)
            _recentSum -= _recent.Dequeue().Litres;
        if (_recent.Count == 0)
            _recentSum = 0;
        return Math.Max(0, _recentSum);
    }

    // Only the latency allowance expires. The balance is conserved: fading a negative credit would turn tolerated
    // excess into newly authorised fluid, repeatedly after every quiet spell or keeper change. -xlinka
    public void Decay(double now) => Recent(now);

    // What the system should hold: its cells once written, plus what is known to have gone in or out that no accepted
    // report has shown yet. The keeper's reports aim its total here (FluidLedger.PublishAccount).
    public double ShouldHold => Ledger.ExpectedTotal() + Credit;

    // The host keeping a system itself: its own proposals draw on the account the way an accepted report does.
    public void Spend(double delta)
    {
        Credit -= delta;
        Reported += delta;
    }

    // Judges one whole report. On a pass its cells become the ledger's proposals and its events come back in events.
    public bool Judge(User sender, IReadOnlyList<FluidReportEntry> report, double now, int ledgerBytesPerSecond, List<FluidReportEntry> events)
    {
        string? why = Check(sender, report, now, ledgerBytesPerSecond, out double delta);
        if (why != null)
        {
            Failed++;
            LastFailure = why;
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Lumora.Core.Logging.Logger.Log($"Fluids: dropped a keeper report for {System.Slot?.Name?.Value ?? "a system"}: {why}.");
            }
            return false;
        }
        Passed++;
        Credit -= delta;
        Reported += delta;
        foreach (var entry in report)
        {
            if (entry.Kind == FluidReportKind.Cell)
                Ledger.Propose(entry.Payload);
            else
                events.Add(entry);
        }
        return true;
    }

    private string? Check(User sender, IReadOnlyList<FluidReportEntry> report, double now, int ledgerBytesPerSecond, out double delta)
    {
        delta = 0;
        if (sender == null || !ReferenceEquals(System.Keeper.Target, sender))
            return "the sender does not keep this system";
        ulong id = System.ReferenceID.RawValue;
        float size = Ledger.CellSize;
        var bounds = System.Bounds.Value;
        bool bounded = bounds.x > 0f || bounds.y > 0f || bounds.z > 0f;
        var centre = System.BoundsCenter.Value;
        int anchors = System.AnchorSlots.Count;
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var entry in report)
        {
            if (entry.System != id)
                return "an entry for another system";
            if (entry.Kind == FluidReportKind.OriginLitres)
            {
                // These are attribution weights, bounded by the ledger total when read. A stale simulator can
                // overestimate them while reconciling without inventing volume or failing an otherwise valid report. -xlinka
                if (entry.Payload.x < 0 || entry.Payload.x >= System.Origins.Count || entry.Payload.y < 0)
                    return "an invalid origin amount";
            }
            if (entry.Kind != FluidReportKind.Cell)
                continue;
            var cell = entry.Payload;
            int anchor = FluidCell.Anchor(cell);
            if (anchor != FluidCell.NoAnchor)
            {
                if (anchor > anchors)
                    return "a cell keyed to an anchor slot the system does not have";
            }
            else if (bounded)
            {
                var p = FluidCell.Centre(cell, size) - centre;
                if (Math.Abs(p.x) > bounds.x * 0.5f + BoundsMargin || Math.Abs(p.y) > bounds.y * 0.5f + BoundsMargin
                    || Math.Abs(p.z) > bounds.z * 0.5f + BoundsMargin)
                    return "a cell outside the system's bounds";
            }
            var key = FluidCell.Key(cell);
            if (!seen.Add(key))
                continue;
            delta += FluidCell.Litres(cell, size) - FluidCell.Litres(Ledger.Expected(key), size);
        }

        int bytes = report.Count * EntryBytes;
        while (_sent.Count > 0 && now - _sent.Peek().At > 1.0)
            _sent.Dequeue();
        int window = bytes;
        foreach (var (_, b) in _sent)
            window += b;
        if (window > ledgerBytesPerSecond * 0.5 * 1.25 + EntryBytes)
            return "over its byte budget";
        _sent.Enqueue((now, bytes));

        // Partial reports are fine both ways: anything from nothing changed up to all that is known, with slack.
        double allowed = Tolerance * Math.Max(Ledger.TotalLitres(), 0) + Ledger.CellLitres + Recent(now);
        double low = Math.Min(0, Credit) - allowed;
        double high = Math.Max(0, Credit) + allowed;
        if (delta < low || delta > high)
            return $"{delta:0.000} L changed against {Credit:0.000} L known";
        return null;
    }
}
