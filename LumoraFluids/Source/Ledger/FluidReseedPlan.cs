// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Particles made again from ledger cells, on a GPU machine: a whole system when this machine starts it (a fresh device
// world, a new layout or tier, an epoch: the packer's placeholder Reseed with no particles marks it), single cells the
// host wrote on its own that the keeper's simulation never held, and the reconcile's refills of cells that came up
// short. Each cell becomes one Reseed command for the solver's commands kernel, which lays its particles in from the
// cell's bottom at rest; the plan hands them out nearest the local head first, at most ParticlesPerFrame a frame, so a
// late joiner's puddle fills in from where they stand. A cell is checked against the ledger again as its turn comes:
// one that emptied or changed meanwhile is taken as it is now, or dropped.
//
// The seed is the system's RefID mixed with the cell's key, so every machine lays the same cell the same way. -xlinka
public sealed class FluidReseedPlan
{
    public const int ParticlesPerFrame = 2048;
    // The reconcile leaves a system alone until this long after its last reseed, so the new particles settle first.
    public const double SettleSeconds = 1.0;

    private readonly List<(FluidSystem System, (int, int, int, int) Key, double Litres, float Distance, bool Whole)> _queue = new();
    private readonly Dictionary<FluidSystem, double> _lastAt = new();
    private readonly Dictionary<FluidSystem, double> _wholeAt = new();
    private readonly Dictionary<FluidSystem, Dictionary<(int, int, int, int), int>> _lookup = new();
    private bool _sorted = true;

    public int Queued => _queue.Count;

    public long CellsReseeded { get; private set; }

    public long ParticlesReseeded { get; private set; }

    public double LitresReseeded { get; private set; }

    public int WholeSystems { get; private set; }

    // Every cell of a system, nearest first, in place of anything queued for it.
    public void All(FluidSystem system, float3 head, double now)
    {
        if (system == null || system.IsDestroyed)
            return;
        Forget(system);
        WholeSystems++;
        float size = FluidLedger.CellSizeOf(system);
        var cells = system.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (Reseedable(cell))
                Add(system, cell, FluidCell.Litres(cell, size), head, size, whole: true);
        }
        _lastAt[system] = now;
        _wholeAt[system] = now;
    }

    // One cell, at most litres of it (the whole cell when litres is 0 or more than it holds).
    public void Cell(FluidSystem system, int4 cell, double litres, float3 head, double now)
    {
        if (system == null || system.IsDestroyed || !Reseedable(cell))
            return;
        float size = FluidLedger.CellSizeOf(system);
        double whole = FluidCell.Litres(cell, size);
        Add(system, cell, litres > 0 && litres < whole ? litres : whole, head, size);
        _lastAt[system] = now;
    }

    private static bool Reseedable(int4 cell)
        => FluidCell.Fill(cell) > 0 && FluidCell.Anchor(cell) == FluidCell.NoAnchor && (FluidCell.Flags(cell) & FluidCell.FlagFading) == 0;

    private void Add(FluidSystem system, int4 cell, double litres, float3 head, float size, bool whole = false)
    {
        var key = FluidCell.Key(cell);
        float distance = system.Slot != null ? float3.Distance(head, system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size))) : 0f;
        _queue.Add((system, key, litres, distance, whole));
        _sorted = false;
    }

    public void Forget(FluidSystem system)
    {
        _queue.RemoveAll(q => ReferenceEquals(q.System, system));
        _lastAt.Remove(system);
        _wholeAt.Remove(system);
    }

    // Litres queued for a system and not laid in yet.
    public double QueuedLitres(FluidSystem system)
    {
        double total = 0;
        foreach (var q in _queue)
            if (ReferenceEquals(q.System, system))
                total += q.Litres;
        return total;
    }

    public bool Queues(FluidSystem system)
    {
        foreach (var q in _queue)
            if (ReferenceEquals(q.System, system))
                return true;
        return false;
    }

    // The whole system is being laid in again, or settling from it: its rounds are only part of it.
    public bool Rebuilding(FluidSystem system, double now)
    {
        foreach (var q in _queue)
            if (q.Whole && ReferenceEquals(q.System, system))
                return true;
        return _wholeAt.TryGetValue(system, out double at) && now - at < SettleSeconds;
    }

    // Reseeding now, or settling from it: the reconcile waits.
    public bool Busy(FluidSystem system, double now)
        => Queues(system) || (_lastAt.TryGetValue(system, out double at) && now - at < SettleSeconds);

    // This frame's share as Reseed commands, nearest first. Returns the particles it asked for.
    public int Pack(FluidSpace space, FluidFrameBlock block, double now)
    {
        if (_queue.Count == 0)
            return 0;
        if (!_sorted)
        {
            _queue.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            _sorted = true;
        }
        _lookup.Clear();
        int budget = ParticlesPerFrame;
        int taken = 0;
        int used = 0;
        var tier = space.Tier;
        while (taken < _queue.Count)
        {
            var (system, key, litres, _, whole) = _queue[taken];
            int index = space.SystemIndex(system);
            if (index < 0 || system.IsDestroyed || system.Slot == null)
            {
                taken++;
                continue;
            }
            if (!Current(system, key, out int4 cell, out int cellIndex))
            {
                taken++;
                continue;
            }
            float size = FluidLedger.CellSizeOf(system);
            litres = Math.Min(litres, FluidCell.Litres(cell, size));
            float d = system.SpacingOn(tier);
            double particleLitres = (double)d * d * d * 1000.0;
            int n = (int)Math.Max(1, Math.Round(litres / particleLitres));
            if (n > budget && used > 0)
                break;
            block.Commands.Add(Command(space, block, system, index, cell, cellIndex, litres, n, size));
            budget -= n;
            used += n;
            taken++;
            CellsReseeded++;
            ParticlesReseeded += n;
            LitresReseeded += litres;
            _lastAt[system] = now;
            if (whole)
                _wholeAt[system] = now;
        }
        _queue.RemoveRange(0, taken);
        return used;
    }

    // The cell as the ledger holds it now, found by its key (one index per system and pack).
    private bool Current(FluidSystem system, (int, int, int, int) key, out int4 cell, out int index)
    {
        var cells = system.Cells;
        if (!_lookup.TryGetValue(system, out var byKey))
        {
            _lookup[system] = byKey = new Dictionary<(int, int, int, int), int>();
            for (int i = 0; i < cells.Count; i++)
                if (FluidCell.Fill(cells[i]) > 0)
                    byKey[FluidCell.Key(cells[i])] = i;
        }
        if (byKey.TryGetValue(key, out index) && index < cells.Count)
        {
            cell = cells[index];
            return Reseedable(cell);
        }
        cell = default;
        index = -1;
        return false;
    }

    private static FluidGpuCommand Command(FluidSpace space, FluidFrameBlock block, FluidSystem system, int index, int4 cell, int cellIndex, double litres, int particles, float size)
    {
        var slot = system.Slot;
        int frame = space.Packer.FrameOf(index);
        var global = slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
        var point = space.Packer.PointToFrame(frame, global);
        float scale = MathF.Abs(slot.GlobalScale.x);
        if (frame > 0 && frame < block.Frames.Count && block.Frames[frame].Scale > 1e-6f)
            scale /= block.Frames[frame].Scale;
        float half = 0.5f * size * scale;
        int tint = cellIndex >= 0 && cellIndex < system.CellTint.Count ? system.CellTint[cellIndex] : 0;
        return new FluidGpuCommand
        {
            Op = (uint)FluidGpuOp.Reseed,
            System = (uint)index,
            Count = (uint)particles,
            Seed = SeedOf(system, cell),
            A = new float4(point, (float)litres),
            B = new float4(half, half, half, 0f),
            C = new float4(BitConverter.Int32BitsToSingle(tint), FluidCell.Origin(cell), FluidCell.Substance(cell), 0f),
        };
    }

    public static uint SeedOf(FluidSystem system, int4 cell)
    {
        ulong h = system.ReferenceID.RawValue * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)cell.x * 0xC2B2AE3D27D4EB4FUL + (h << 6) + (h >> 2);
        h ^= (ulong)(uint)cell.y * 0x165667B19E3779F9UL + (h << 6) + (h >> 2);
        h ^= (ulong)(uint)FluidCell.Iy(cell) * 0x27D4EB2F165667C5UL + (h << 6) + (h >> 2);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (uint)h;
    }
}
