// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One machine's rounds of GPU bins, put together from the summaries they ride in, 192 a page. The ledger bin kernel
// bins every system's particles into ledger cells (on the keeper at 4 Hz, on every other GPU machine at 2 Hz for the
// reconcile) and pages the round out one summary at a time, with RoundFlag set in the summary header's Reserved0 on
// every page it wrote, an empty round included. A summary without the flag carries no bins, so "nothing binned" and
// "binned and found nothing" never look alike.
//
// A bin is one ledger cell's share of one origin and one substance: Cell in the ledger's format with the fill of that
// share (at most a whole cell), Index the particle count (low 16 bits) and strand particles (high 16), Tint the mean
// speed in mm/s (low 16) and the fill past a whole cell (high 16), Flags the highest particle's height in the system's
// space (float bits).
//
// Fluid pressed together holds more than a cell's volume in a cell, and a cell's fill stops at a whole cell. So a
// round becomes ledger cells the same way on every machine: the shares of a cell add up (the largest names the origin
// and the substance, and the cell moves when any share does), then a cell past a whole cell hands the rest up its
// column. The ledger keeps every litre, and the keeper's ledger and a follower's own bins are shaped alike. -xlinka
public sealed class FluidBinRound
{
    // FluidSummaryHeader.Reserved0: this summary carries a page of a bin round (BinCursor, BinTotal, Bins).
    public const uint RoundFlag = 1u;
    public const int CountMask = 0xFFFF;
    public const int StrandShift = 16;
    public const int OverflowShift = 16;

    private readonly List<FluidLedgerEntry> _building = new();
    private bool _open;

    // The last complete round, in arrival order.
    public List<FluidLedgerEntry> Entries { get; } = new();

    public int Rounds { get; private set; }

    public int PagesDropped { get; private set; }

    public static bool Carries(in FluidSummaryHeader header) => (header.Reserved0 & RoundFlag) != 0;

    public static int CountOf(in FluidLedgerEntry bin) => (int)(bin.Index & CountMask);

    public static int StrandsOf(in FluidLedgerEntry bin) => (int)(bin.Index >> StrandShift);

    public static float SpeedOf(in FluidLedgerEntry bin) => (bin.Tint & 0xFFFF) * 0.001f;

    public static int OverflowOf(in FluidLedgerEntry bin) => (int)(bin.Tint >> OverflowShift);

    // The share's fill with what it holds past a whole cell.
    public static int FullFillOf(in FluidLedgerEntry bin) => FluidCell.Fill(bin.Cell) + OverflowOf(bin);

    public static float TopOf(in FluidLedgerEntry bin) => BitConverter.Int32BitsToSingle((int)bin.Flags);

    // Takes one summary's page. True when it completed a round, which is then in Entries.
    public bool Take(in FluidSummaryView summary)
    {
        if (summary.IsEmpty)
            return false;
        var header = summary.Header;
        if (!Carries(header))
            return false;
        if (header.BinCursor == 0)
        {
            _building.Clear();
            _open = true;
        }
        else if (!_open || header.BinCursor != _building.Count)
        {
            _open = false;
            PagesDropped++;
            return false;
        }
        foreach (var bin in summary.Bins)
            _building.Add(bin);
        if (_building.Count < header.BinTotal)
            return false;
        _open = false;
        Entries.Clear();
        Entries.AddRange(_building);
        _building.Clear();
        Rounds++;
        return true;
    }

    // Adds one share into a map of cells by key: (cell, total fill, the largest share's fill).
    public static void Add(Dictionary<(int, int, int, int), (int4 Cell, int Fill, int Lead)> cells, int4 cell, int fill)
    {
        var key = FluidCell.Key(cell);
        if (!cells.TryGetValue(key, out var have))
        {
            cells[key] = (cell, fill, fill);
            return;
        }
        var lead = fill > have.Lead ? cell : have.Cell;
        bool moving = FluidCell.IsMoving(cell) || FluidCell.IsMoving(have.Cell);
        cells[key] = (moving ? FluidCell.WithFlags(lead, FluidCell.FlagMoving) : lead, have.Fill + fill, Math.Max(fill, have.Lead));
    }

    // Every cell past a whole cell hands the rest to the cell above it, made when there is none (it takes the origin,
    // substance and flags of the cell it came from). Totals are unchanged.
    public static void Stack(Dictionary<(int, int, int, int), (int4 Cell, int Fill, int Lead)> cells)
    {
        List<(int, int, int, int)>? over = null;
        foreach (var (key, value) in cells)
            if (value.Fill > FluidCell.FillMax)
                (over ??= new()).Add(key);
        if (over == null)
            return;
        // lowest first, so a column's carry rises through it once
        over.Sort(static (a, b) => a.Item2.CompareTo(b.Item2));
        foreach (var start in over)
        {
            var key = start;
            if (!cells.TryGetValue(key, out var value) || value.Fill <= FluidCell.FillMax)
                continue;
            int carry = value.Fill - FluidCell.FillMax;
            cells[key] = (value.Cell, FluidCell.FillMax, value.Lead);
            var below = value.Cell;
            for (int guard = 0; carry > 0 && guard < 64; guard++)
            {
                key = (key.Item1, key.Item2 + 1, key.Item3, key.Item4);
                if (cells.TryGetValue(key, out var above))
                {
                    int total = above.Fill + carry;
                    carry = Math.Max(0, total - FluidCell.FillMax);
                    cells[key] = (above.Cell, Math.Min(total, FluidCell.FillMax), above.Lead);
                    below = above.Cell;
                }
                else
                {
                    var made = FluidCell.Pack(key.Item1, key.Item2, key.Item3, 0, FluidCell.Origin(below), FluidCell.Substance(below),
                        FluidCell.Flags(below), key.Item4, FluidCell.Age(below));
                    int fill = Math.Min(carry, FluidCell.FillMax);
                    cells[key] = (made, fill, fill);
                    carry -= fill;
                    below = made;
                }
            }
        }
    }

    // The map as ledger cells, each with its fill.
    public static int4[] Cells(Dictionary<(int, int, int, int), (int4 Cell, int Fill, int Lead)> cells)
    {
        var output = new int4[cells.Count];
        int i = 0;
        foreach (var (_, value) in cells)
            output[i++] = FluidCell.WithFill(value.Cell, Math.Min(value.Fill, FluidCell.FillMax));
        return output;
    }
}
