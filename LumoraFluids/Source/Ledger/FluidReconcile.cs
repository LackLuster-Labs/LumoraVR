// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A GPU machine that does not keep a system pulls its own particles toward the ledger, every bin round (2 Hz). The
// round is this machine's own GPU bins of its particles, cell by cell; the ledger is the cells the host wrote. A cell
// whose local and ledger litres differ by more than max(10% of a cell, one particle) for two rounds in a row is acted
// on, at most MaxParticles particles a round:
//   - too much here: particles go from the top of the cell first (FluidLedgerReconcile on the GPU), all of them when
//     the ledger has no such cell;
//   - too little, with particles here: they grow toward the ledger's amount, at most half again (FluidReseed);
//   - too little, with few or none here: the deficit is reseeded from the cell's bottom (the solver's Reseed command).
// It leaves alone: cells the ledger marks Moving or Fading, cells whose particles here average more than 5 cm/s, cells
// holding strand particles, cells within 30 cm of the local user's body and for 2 s after, cells acted on within the
// last second, and any system still reseeding. Near me, my own simulation wins.
//
// The bins are read the way the keeper's become the ledger (FluidBinRound: shares added, what a cell holds past a whole
// cell handed up its column), so pressed-together water here does not read as a cell too full and one too empty. And
// it only ever moves a system's total toward the ledger's: while this machine holds clearly more in all than the
// ledger, nothing is added anywhere, and while it holds clearly less, nothing is taken. Cells that cannot be acted on
// (moving water) otherwise leave the rest one-sided, and the total runs away.
//
// The keeper reconciles too, but only its total, against the host's account rather than the ledger's cells (where
// fluid is, it decides). Its reports never carry fluid its simulation made or lost (FluidKeeperReport), so those cells
// stay different from the ledger; once its total stands clearly off the account (3% or a cell, plus the account's own
// recent movement), it acts on them the same way, cells it has entries in flight for left out, and no further than
// the total is off.
//
// Actions are ledger entries for the GPU: System the system index, Index the kind, Tint the amount (float bits:
// the share to fade, or the growth factor), Flags the middle of the cell's fluid here in the system's space and
// Cell.w half its height (float bits both), Cell x, y and z the cell's key. Each acted cell also gets a wake box, so
// its sleeping particles take the change. -xlinka
public sealed class FluidReconcile
{
    public const uint ActionTrim = 0;
    public const uint ActionGrow = 1;
    public const float Disagreement = 0.10f;
    public const float MovingSpeed = 0.05f;
    public const float NearPeople = 0.3f;
    public const double NearHold = 2.0;
    public const double ActedHold = 1.0;
    public const int StrikesToAct = 2;
    public const int MaxParticles = 256;
    public const float MaxGrowth = 1.5f;
    // A cell with less than this share of the ledger's amount here is reseeded rather than grown.
    public const float GrowFloor = 0.5f;
    // How far a system's total here may be from the ledger's before one direction of action stops.
    public const float SystemTolerance = 0.03f;

    private readonly Dictionary<(FluidSystem, (int, int, int)), int> _strikes = new();
    private readonly Dictionary<(FluidSystem, (int, int, int)), double> _nearUntil = new();
    private readonly Dictionary<(FluidSystem, (int, int, int)), double> _actedAt = new();
    private readonly Dictionary<(int, int, int), (double Litres, int Count, int Strands, float SpeedSum, float Top)> _local = new();
    private readonly Dictionary<(int, int, int), (int Index, int4 Cell)> _ledger = new();
    private readonly List<float3> _body = new();
    private readonly HashSet<(int, int, int)> _keys = new();
    private readonly Dictionary<(int, int, int, int), (int4 Cell, int Fill, int Lead)> _stack = new();
    private readonly Dictionary<(int, int, int), (int Count, int Strands, float SpeedSum, float Top)> _extra = new();

    // Off makes this machine keep its own simulation whatever the ledger says (the mutation control).
    public bool Enabled { get; set; } = true;

    public int Rounds { get; private set; }

    public long Trims { get; private set; }

    public long Grows { get; private set; }

    public long Refills { get; private set; }

    public long Skipped { get; private set; }

    // Rounds the keeper found its own total clearly off the account, and the actions it took on them.
    public long KeeperRounds { get; private set; }

    public long KeeperActions { get; private set; }

    // Actions not taken because they would move the system's total away from the ledger's.
    public long Held { get; private set; }

    // The last followed system's totals, here and in the ledger, as the last round saw them.
    public double LastLocalLitres { get; private set; }

    public double LastLedgerLitres { get; private set; }

    // Cells whose two amounts agreed in the last round, and those that did not, over every followed system.
    public int LastAgreed { get; private set; }

    public int LastDisagreed { get; private set; }

    // Systems whose total stood clearly off in the last round (the ledger's, or for the keeper the account).
    public int LastOff { get; private set; }

    public void Forget(FluidSystem system)
    {
        Drop(_strikes, system);
        Drop(_nearUntil, system);
        Drop(_actedAt, system);
    }

    private static void Drop<T>(Dictionary<(FluidSystem, (int, int, int)), T> map, FluidSystem system)
    {
        List<(FluidSystem, (int, int, int))>? gone = null;
        foreach (var key in map.Keys)
            if (ReferenceEquals(key.Item1, system))
                (gone ??= new()).Add(key);
        if (gone != null)
            foreach (var key in gone)
                map.Remove(key);
    }

    // One complete round of this machine's bins. Actions for the GPU go to actions, wake boxes to wakes (system index,
    // centre and half size in the system's frame) and refills to the plan.
    public void Round(FluidSpace space, IReadOnlyList<FluidLedgerEntry> round, double now, FluidReseedPlan plan,
        List<FluidLedgerEntry> actions, List<FluidGpuCommand> wakes, FluidLedgerWorld? keeping = null)
    {
        Rounds++;
        LastAgreed = 0;
        LastDisagreed = 0;
        LastOff = 0;
        if (!Enabled)
            return;
        Body(space.World);
        var head = space.World.LocalUser?.Root?.HeadPosition ?? float3.Zero;
        int budget = MaxParticles;
        var systems = space.Systems;
        for (int s = 0; s < systems.Count && budget > 0; s++)
        {
            var system = systems[s];
            if (system.IsDestroyed || system.Slot == null || !system.Simulate.Value)
                continue;
            if (plan.Busy(system, now))
                continue;
            if (system.IsKeeperHere)
            {
                if (keeping == null || !keeping.KeeperAccount(system, out double account, out double tolerance))
                    continue;
                Gather(system, s, round);
                budget = Compare(space, system, s, now, plan, head, budget, actions, wakes, (account, tolerance, keeping.Report));
                continue;
            }
            Gather(system, s, round);
            budget = Compare(space, system, s, now, plan, head, budget, actions, wakes, null);
        }
    }

    private void Body(World world)
    {
        _body.Clear();
        var root = world.LocalUser?.Root;
        if (root == null || root.IsDestroyed)
            return;
        _body.Add(root.HeadPosition);
        _body.Add(root.HipsPosition);
        _body.Add(root.FeetPosition);
        foreach (var slot in new[] { root.LeftHandSlot, root.RightHandSlot, root.LeftFootSlot, root.RightFootSlot })
            if (slot is { IsDestroyed: false })
                _body.Add(slot.GlobalPosition);
    }

    private void Gather(FluidSystem system, int index, IReadOnlyList<FluidLedgerEntry> round)
    {
        _local.Clear();
        _ledger.Clear();
        _stack.Clear();
        _extra.Clear();
        float size = FluidLedger.CellSizeOf(system);
        foreach (var bin in round)
        {
            if (bin.System != (uint)index || FluidCell.Anchor(bin.Cell) != FluidCell.NoAnchor)
                continue;
            FluidBinRound.Add(_stack, bin.Cell, FluidBinRound.FullFillOf(bin));
            var key = (bin.Cell.x, FluidCell.Iy(bin.Cell), bin.Cell.y);
            int count = FluidBinRound.CountOf(bin);
            _extra.TryGetValue(key, out var have);
            _extra[key] = (have.Count + count, have.Strands + FluidBinRound.StrandsOf(bin), have.SpeedSum + FluidBinRound.SpeedOf(bin) * count,
                have.Count == 0 ? FluidBinRound.TopOf(bin) : MathF.Max(have.Top, FluidBinRound.TopOf(bin)));
        }
        FluidBinRound.Stack(_stack);
        foreach (var (key4, value) in _stack)
        {
            var key = (key4.Item1, key4.Item2, key4.Item3);
            _extra.TryGetValue(key, out var e);
            _local[key] = (value.Fill / (double)FluidCell.FillMax * FluidCell.CellLitres(size), e.Count, e.Strands, e.SpeedSum, e.Top);
        }
        var cells = system.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                continue;
            _ledger[(cell.x, FluidCell.Iy(cell), cell.y)] = (i, cell);
        }
    }

    private int Compare(FluidSpace space, FluidSystem system, int index, double now, FluidReseedPlan plan, float3 head, int budget,
        List<FluidLedgerEntry> actions, List<FluidGpuCommand> wakes, (double Account, double Tolerance, FluidKeeperReport Report)? keeper)
    {
        float size = FluidLedger.CellSizeOf(system);
        double cellLitres = FluidCell.CellLitres(size);
        float d = system.SpacingOn(space.Tier);
        double particleLitres = (double)d * d * d * 1000.0;
        double threshold = Math.Max(Disagreement * cellLitres, particleLitres);
        float scale = MathF.Abs(system.Slot.GlobalScale.x);
        var slot = system.Slot;
        double localTotal = 0, ledgerTotal = 0;
        foreach (var (_, value) in _local)
            localTotal += value.Litres;
        foreach (var (_, value) in _ledger)
            ledgerTotal += FluidCell.Litres(value.Cell, size);
        double systemDiff = localTotal - ledgerTotal;
        double systemSlack = Math.Max(SystemTolerance * ledgerTotal, cellLitres);
        bool mayAdd = systemDiff < systemSlack;
        bool mayTake = systemDiff > -systemSlack;
        if (!mayAdd || !mayTake)
            LastOff++;
        // The keeper: one way only, and only as far as its total is off the account.
        double room = double.PositiveInfinity;
        if (keeper is { } k)
        {
            double off = localTotal - k.Account;
            mayTake = off > k.Tolerance;
            mayAdd = off < -k.Tolerance;
            if (!mayTake && !mayAdd)
                return budget;
            LastOff++;
            room = Math.Abs(off);
            KeeperRounds++;
        }
        LastLocalLitres = localTotal;
        LastLedgerLitres = ledgerTotal;
        _keys.Clear();
        foreach (var key in _local.Keys)
            _keys.Add(key);
        foreach (var key in _ledger.Keys)
            _keys.Add(key);
        foreach (var key in _keys)
        {
            if (budget <= 0 || room <= 0)
                break;
            if (keeper is { } inFlight && inFlight.Report.InFlight(system, (key.Item1, key.Item2, key.Item3, FluidCell.NoAnchor)))
                continue;
            _local.TryGetValue(key, out var local);
            bool inLedger = _ledger.TryGetValue(key, out var entry);
            double ledgerLitres = inLedger ? FluidCell.Litres(entry.Cell, size) : 0;
            double diff = local.Litres - ledgerLitres;
            var id = (system, key);
            if (Math.Abs(diff) <= threshold)
            {
                LastAgreed++;
                _strikes.Remove(id);
                continue;
            }
            LastDisagreed++;
            var centre = new float3((key.Item1 + 0.5f) * size, (key.Item2 + 0.5f) * size, (key.Item3 + 0.5f) * size);
            var global = slot.LocalPointToGlobal(centre);
            if (Near(global))
                _nearUntil[id] = now + NearHold;
            bool moving = inLedger && (FluidCell.IsMoving(entry.Cell) || (FluidCell.Flags(entry.Cell) & FluidCell.FlagFading) != 0);
            bool fast = local.Count > 0 && local.SpeedSum / local.Count > MovingSpeed;
            if (moving || fast || local.Strands > 0 || (_nearUntil.TryGetValue(id, out double near) && now < near)
                || (_actedAt.TryGetValue(id, out double acted) && now - acted < ActedHold))
            {
                _strikes.Remove(id);
                Skipped++;
                continue;
            }
            int strikes = _strikes.GetValueOrDefault(id) + 1;
            if (strikes < StrikesToAct)
            {
                _strikes[id] = strikes;
                continue;
            }
            _strikes.Remove(id);
            if ((diff > 0 && !mayTake) || (diff < 0 && !mayAdd))
            {
                Held++;
                continue;
            }
            if (Math.Abs(diff) > room)
                diff = Math.Sign(diff) * room;
            room -= Math.Abs(diff);
            if (keeper != null)
                KeeperActions++;
            int particles = (int)Math.Max(1, Math.Round(Math.Abs(diff) / particleLitres));
            // The fluid here lies from about half a particle above the cell's floor up to its highest particle.
            float lowest = key.Item2 * size + 0.5f * d;
            float top = local.Count > 0 ? MathF.Max(local.Top, lowest) : centre.y;
            float middle = 0.5f * (lowest + top);
            float halfHeight = MathF.Max(0.5f * (top - lowest), 0.5f * d);
            var keyCell = FluidCell.Pack(key.Item1, key.Item2, key.Item3, 0, 0, 0, 0, 0, 0);
            keyCell = new int4(keyCell.x, keyCell.y, keyCell.z, BitConverter.SingleToInt32Bits(halfHeight));
            if (diff > 0)
            {
                float share = (float)Math.Clamp(diff / Math.Max(local.Litres, 1e-12), 0, 1);
                actions.Add(new FluidLedgerEntry { System = (uint)index, Index = ActionTrim, Tint = Bits(share), Flags = Bits(middle), Cell = keyCell });
                Trims++;
            }
            else if (local.Count > 0 && local.Litres >= GrowFloor * ledgerLitres)
            {
                float growth = (float)Math.Min((local.Litres - diff) / Math.Max(local.Litres, 1e-12), MaxGrowth);
                actions.Add(new FluidLedgerEntry { System = (uint)index, Index = ActionGrow, Tint = Bits(growth), Flags = Bits(middle), Cell = keyCell });
                Grows++;
            }
            else
            {
                plan.Cell(system, entry.Cell, -diff, head, now);
                Refills++;
            }
            budget -= particles;
            _actedAt[id] = now;
            wakes.Add(Wake(space, system, index, global, 0.75f * size * scale));
        }
        return budget;
    }

    private bool Near(float3 global)
    {
        float r2 = NearPeople * NearPeople;
        foreach (var p in _body)
            if (float3.DistanceSquared(p, global) < r2)
                return true;
        return false;
    }

    private static FluidGpuCommand Wake(FluidSpace space, FluidSystem system, int index, float3 global, float half)
    {
        int frame = space.Packer.FrameOf(index);
        var point = space.Packer.PointToFrame(frame, global);
        return new FluidGpuCommand
        {
            Op = (uint)FluidGpuOp.WakeBox,
            System = (uint)index,
            A = new float4(point, 0f),
            B = new float4(half, half, half, 0f),
        };
    }

    private static uint Bits(float v) => (uint)BitConverter.SingleToInt32Bits(v);
}
