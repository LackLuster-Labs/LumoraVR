// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The host's mirror of one system's ledger, at 4 Hz while anything is awake. What the keeper's bins say a cell
// holds arrives as a proposal; the pass writes the proposals that matter into Cells, inside the system bypass,
// within LedgerBytesPerSecond: the largest differences nearest people first, a Moving cell at most once a second, a
// cell whose fill moved less than 2% (settled) or 10% (Moving) of a cell not at all unless its kind changed.
//
// Cells only ever change in place. An entry never moves and is never removed: an emptied cell keeps fill 0 and joins
// the free list, and a new cell takes a free index first. When there is none the array grows by 64 in a send of its
// own, with the new cells' values riding in the appended tail, so the wire only ever carries a sparse write or an
// append. A grow in the same send as a value write would go out as the whole array, about 29 KB for a big tub.
//
// Origins are per pour: (user RefID, pour number, flags) at an index the cells carry. They mean nothing in another
// session, so on the first sight of a system the host starts them over, and origin indices the loaded cells still
// carry read as the world's until those cells are written again. -xlinka
public sealed class FluidLedger
{
    public const float SettledThreshold = 0.02f;
    public const float MovingThreshold = 0.10f;
    public const int GrowBy = 64;
    public const int MinGrowBy = 16;
    public const int RefillBelow = 16;
    public const double MovingInterval = 1.0;
    // a sparse entry: a 7-bit index (2 bytes past 127) and the 16 bytes of the int4
    public const int SparseEntryBytes = 18;
    public const int AppendEntryBytes = 16;
    public const int MaxOrigins = 255;
    public const int OriginScript = 1 << 24;
    public const int OriginFading = 1 << 25;
    public const int OriginMeasured = 1 << 26;
    // Standalone and Low simulate at 1.6 x the High spacing, the coarsest any keeper runs.
    public const float CoarsestSpacingScale = 1.6f;
    // A cell the host wrote on its own (a spawn fill, a judged report) is not emptied by a keeper's bins for this long,
    // which gives the keeper's simulation time to reseed it.
    public const double ForeignHold = 2.0;
    public const int AccountPublished = 1;
    public const double AccountInterval = 0.25;
    // the account is rewritten once it moved by this share of itself (or a millilitre)
    public const double AccountStep = 0.0005;

    private readonly Dictionary<(int, int, int, int), int> _index = new();
    private readonly Stack<int> _free = new();
    private readonly Dictionary<(int, int, int, int), int4> _want = new();
    private readonly Dictionary<int, double> _movingWrittenAt = new();
    private readonly List<((int, int, int, int) Key, int4 Cell, float Score, int Index)> _candidates = new();
    private readonly HashSet<int> _staleOrigins = new();
    private readonly Dictionary<(int, int, int, int), double> _foreignAt = new();
    private readonly FluidLedgerRule? _rule;
    private int _indexedCount = -1;
    private double _tokens;
    private int _growBy = GrowBy;
    private double _lastPass = double.NaN;
    private double _accountAt = double.NegativeInfinity;
    private bool _originsReset;

    public FluidLedger(FluidSystem system, FluidLedgerRule? rule = null)
    {
        System = system;
        _rule = rule;
    }

    public FluidSystem System { get; }

    public World World => System.World;

    public float CellSize => CellSizeOf(System);

    public double CellLitres => FluidCell.CellLitres(CellSize);

    // Sends this mirror made, for the bandwidth checks.
    public long BytesWritten { get; private set; }

    public int CellsWritten { get; private set; }

    public int Grows { get; private set; }

    public int Pending => _want.Count;

    public int FreeCount => _free.Count;

    // The spacing every keeper's bins can resolve: the coarsest tier's, held by the spacing every tier keeps at least
    // (MaxSpacing, or the thinnest spawn volume's depth over 3 when that is 0), the same cap the tiers themselves use.
    public static float LedgerSpacingOf(FluidSystem system)
    {
        float spacing = system.HighSpacing * CoarsestSpacingScale;
        float max = system.EffectiveMaxSpacing;
        if (max > 0f && spacing > max)
            spacing = max;
        return Math.Max(spacing, 0.002f);
    }

    public static float CellSizeOf(FluidSystem system)
    {
        float size = system.CellSize.Value;
        return size > 0f ? size : 3f * LedgerSpacingOf(system);
    }

    // The slot a cell keyed to an anchor lies in: anchor index a (1..255) is AnchorSlots entry a - 1, a packed RefID.
    // Null for the system's own space (0), an index past the list, or a slot that is gone. Any machine.
    public static Slot? AnchorSlotOf(FluidSystem system, int anchor)
    {
        if (system == null || anchor <= FluidCell.NoAnchor || system.World == null)
            return null;
        var slots = system.AnchorSlots;
        if (anchor - 1 >= slots.Count)
            return null;
        var packed = slots[anchor - 1];
        ulong id = FluidRings.Join(packed.x, packed.y);
        if (id == 0)
            return null;
        return system.World.ReferenceController.GetObjectOrNull(new RefID(id)) is Slot { IsDestroyed: false } slot ? slot : null;
    }

    // The slot a cell's coordinates are in: its anchor slot, else the system's own.
    public static Slot? SpaceOf(FluidSystem system, int4 cell)
        => FluidCell.Anchor(cell) == FluidCell.NoAnchor ? system?.Slot : AnchorSlotOf(system!, FluidCell.Anchor(cell));

    // Cells the host wrote on its own (a spawn fill, a judged report) in the last ForeignHold seconds, by key.
    public bool WroteOnItsOwn((int, int, int, int) key) => _foreignAt.TryGetValue(key, out double at) && World.Time.TotalTime - at < ForeignHold;

    // What the host would hold for a cell once its pending proposal is written.
    public int4 Expected((int, int, int, int) key)
    {
        if (_want.TryGetValue(key, out var want))
            return want;
        return _index.TryGetValue(key, out int i) && i < System.Cells.Count ? System.Cells[i] : default;
    }

    public bool TryGetIndex((int, int, int, int) key, out int index)
    {
        Reindex();
        return _index.TryGetValue(key, out index);
    }

    public void Propose(int4 cell)
    {
        _foreignAt[FluidCell.Key(cell)] = World.Time.TotalTime;
        Want(cell);
    }

    private void Want(int4 cell)
    {
        Reindex();
        var key = FluidCell.Key(cell);
        if (FluidCell.Fill(cell) == 0 && !_index.ContainsKey(key))
        {
            _want.Remove(key);
            return;
        }
        _want[key] = cell;
    }

    // A whole binned state: every cell it holds is proposed, and every cell the ledger has that it lacks is proposed
    // empty, apart from cells the host wrote on its own a moment ago, which the keeper may not have reseeded yet. Only
    // for a complete set of bins.
    public void ProposeComplete(ReadOnlySpan<int4> cells, int anchor = FluidCell.NoAnchor)
    {
        Reindex();
        double now = World.Time.TotalTime;
        var seen = new HashSet<(int, int, int, int)>();
        foreach (var cell in cells)
        {
            Want(cell);
            seen.Add(FluidCell.Key(cell));
        }
        foreach (var (key, index) in _index)
        {
            if (key.Item4 != anchor || seen.Contains(key))
                continue;
            if (_foreignAt.TryGetValue(key, out double at) && now - at < ForeignHold)
                continue;
            var current = System.Cells[index];
            if (FluidCell.Fill(current) > 0)
                _want[key] = FluidCell.WithFill(current, 0);
        }
    }

    public void ClearPending() => _want.Clear();

    // One mirror pass. people are world positions. Returns the bytes it put on the wire.
    public int Pass(double now, int bytesPerSecond, IReadOnlyList<float3>? people = null)
    {
        Reindex();
        double rate = Math.Max(256, bytesPerSecond);
        // At most a quarter second's worth saved up, so no second ever carries more than 1.25 x the rate. A grow goes
        // out whole, so it takes as many cells as that allows (MinGrowBy at the least, GrowBy at the most).
        double cap = Math.Max(rate * 0.25, 4 + MinGrowBy * AppendEntryBytes);
        _growBy = Math.Clamp((int)((cap - 4) / AppendEntryBytes), MinGrowBy, GrowBy);
        if (double.IsNaN(_lastPass))
            _tokens = cap;
        else
            _tokens = Math.Min(cap, _tokens + rate * Math.Max(0, now - _lastPass));
        _lastPass = now;
        if (_foreignAt.Count > 0)
        {
            List<(int, int, int, int)>? old = null;
            foreach (var (key, at) in _foreignAt)
                if (now - at >= ForeignHold)
                    (old ??= new()).Add(key);
            if (old != null)
                foreach (var key in old)
                    _foreignAt.Remove(key);
        }
        if (_want.Count == 0)
            return 0;
        // A grow goes out alone, and so does a value pass after it. A world with no session sends nothing, so nothing
        // there ever clears the dirty mark and the next grow would wait for ever.
        if (System.Cells.IsSyncDirty && World.Session != null)
            return 0;

        Gather(now, people);
        if (_candidates.Count == 0)
            return 0;

        int fresh = 0;
        foreach (var c in _candidates)
            if (c.Index < 0)
                fresh++;
        int spent;
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            spent = fresh > _free.Count || (fresh > 0 && _free.Count < RefillBelow)
                ? Grow()
                : WriteValues();
        }
        if (spent > 0)
            _rule?.Close(System);
        return spent;
    }

    private void Gather(double now, IReadOnlyList<float3>? people)
    {
        _candidates.Clear();
        var cells = System.Cells;
        float size = CellSize;
        var drop = new List<(int, int, int, int)>();
        foreach (var (key, want) in _want)
        {
            int index = _index.TryGetValue(key, out int i) ? i : -1;
            float diff;
            if (index >= 0)
            {
                var current = cells[index];
                bool moving = FluidCell.IsMoving(current) || FluidCell.IsMoving(want);
                diff = Math.Abs(FluidCell.Fill(want) - FluidCell.Fill(current)) / (float)FluidCell.FillMax;
                bool kindChanged = !FluidCell.SameKind(want, current) && (FluidCell.Fill(want) > 0 || FluidCell.Fill(current) > 0);
                if (!kindChanged && diff <= (moving ? MovingThreshold : SettledThreshold))
                {
                    // The keeper already accounted for this proposal. Keep a small unwritten remainder;
                    // dropping it would change the account without any liquid moving. -xlinka
                    if (current == want)
                        drop.Add(key);
                    continue;
                }
                if (moving && _movingWrittenAt.TryGetValue(index, out double at) && now - at < MovingInterval)
                    continue;
                if (kindChanged)
                    diff = Math.Max(diff, 1f);
            }
            else
            {
                if (FluidCell.Fill(want) == 0)
                {
                    drop.Add(key);
                    continue;
                }
                diff = FluidCell.Fill(want) / (float)FluidCell.FillMax;
            }
            float distance = NearestPerson(want, size, people);
            _candidates.Add((key, want, diff / (1f + distance), index));
        }
        foreach (var key in drop)
            _want.Remove(key);
        _candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
    }

    private float NearestPerson(int4 cell, float size, IReadOnlyList<float3>? people)
    {
        if (people == null || people.Count == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor || System.Slot == null)
            return 0f;
        var centre = System.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
        float best = float.MaxValue;
        foreach (var p in people)
            best = Math.Min(best, float3.Distance(p, centre));
        return best;
    }

    // Appends as many cells as the rate allows (up to GrowBy), the most important new ones with their values and the
    // rest empty, as one append.
    private int Grow()
    {
        int cost = 4 + _growBy * AppendEntryBytes;
        if (_tokens < cost)
            return 0;
        var cells = System.Cells;
        int start = cells.Count;
        var tail = new int4[_growBy];
        int used = 0;
        var placed = new List<((int, int, int, int) Key, int Index)>();
        foreach (var c in _candidates)
        {
            if (c.Index >= 0 || used >= _growBy)
                continue;
            tail[used] = c.Cell;
            placed.Add((c.Key, start + used));
            used++;
        }
        cells.AddRange(tail);
        if (System.MixColours.Value)
            GrowTints(start + _growBy);
        foreach (var (key, index) in placed)
        {
            _index[key] = index;
            _want.Remove(key);
        }
        for (int i = _growBy - 1; i >= used; i--)
            _free.Push(start + i);
        _indexedCount = cells.Count;
        _tokens -= cost;
        Grows++;
        CellsWritten += used;
        BytesWritten += cost;
        return cost;
    }

    private void GrowTints(int length)
    {
        var tints = System.CellTint;
        if (tints.Count < length)
            tints.AddRange(new int[length - tints.Count]);
    }

    private int WriteValues()
    {
        var cells = System.Cells;
        int spent = 0;
        foreach (var c in _candidates)
        {
            if (_tokens < SparseEntryBytes)
                break;
            int index = c.Index;
            if (index < 0)
            {
                if (_free.Count == 0)
                    continue;
                index = _free.Pop();
                _index[c.Key] = index;
            }
            cells[index] = c.Cell;
            if (FluidCell.Fill(c.Cell) == 0)
            {
                _index.Remove(c.Key);
                _free.Push(index);
                _movingWrittenAt.Remove(index);
            }
            else if (FluidCell.IsMoving(c.Cell))
            {
                _movingWrittenAt[index] = _lastPass;
            }
            _want.Remove(c.Key);
            _tokens -= SparseEntryBytes;
            spent += SparseEntryBytes;
            CellsWritten++;
        }
        BytesWritten += spent;
        return spent;
    }

    // The index is rebuilt whenever Cells changed length behind this mirror: a load, or the creator's writes in its
    // window. After that the host is the only writer and keeps the index itself.
    public void Reindex(bool force = false)
    {
        var cells = System.Cells;
        if (!force && cells.Count == _indexedCount)
            return;
        _index.Clear();
        _free.Clear();
        for (int i = cells.Count - 1; i >= 0; i--)
        {
            var cell = cells[i];
            if (FluidCell.Fill(cell) == 0)
                _free.Push(i);
            else
                _index[FluidCell.Key(cell)] = i;
        }
        _indexedCount = cells.Count;
    }

    // TOTALS, from what the cells hold right now.

    // What the cells will hold once every pending proposal is written.
    public double ExpectedTotal()
    {
        Reindex();
        double total = 0;
        float size = CellSize;
        var cells = System.Cells;
        for (int i = 0; i < cells.Count; i++)
            total += FluidCell.Litres(cells[i], size);
        foreach (var (key, want) in _want)
        {
            total += FluidCell.Litres(want, size);
            if (_index.TryGetValue(key, out int i) && i < cells.Count)
                total -= FluidCell.Litres(cells[i], size);
        }
        return total;
    }

    // The cells as they will be once every pending proposal is written, by key; empty ones left out.
    public void ExpectedCells(Dictionary<(int, int, int, int), int4> output)
    {
        Reindex();
        output.Clear();
        foreach (var cell in System.Cells)
            if (FluidCell.Fill(cell) > 0)
                output[FluidCell.Key(cell)] = cell;
        foreach (var (key, want) in _want)
        {
            if (FluidCell.Fill(want) > 0)
                output[key] = want;
            else
                output.Remove(key);
        }
    }

    public bool AccountDue(double now) => now - _accountAt >= AccountInterval;

    // THE ACCOUNT (host): the litres the system should hold, for its keeper. It is the world origin's entry,
    // Origins[0].w holds millilitres, z is AccountPublished. Its spare x/y words hold the measured world-origin
    // amount and marker when available; user and pour lookups start at 1. -xlinka
    // It moves with known flows only (pours, emits, removes, evaporation, drains, pickups, releases, the host's own
    // writes), never with a keeper's report, so a keeper whose simulation gained or lost fluid cannot carry that into
    // the ledger. Written when it changes, at most AccountInterval apart; a settled system writes nothing.
    public int PublishAccount(double litres, double now)
    {
        if (!World.IsAuthority || now - _accountAt < AccountInterval || double.IsNaN(litres))
            return 0;
        var origins = System.Origins;
        int ml = (int)Math.Clamp(Math.Round(litres * 1000.0), 0, int.MaxValue);
        if (origins.Count > 0)
        {
            var was = origins[0];
            if (was.z == AccountPublished && Math.Abs(was.w - ml) < Math.Max(1, (int)(ml * AccountStep)))
                return 0;
        }
        _accountAt = now;
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            var first = origins.Count > 0 ? origins[0] : default;
            var entry = new int4(first.x, first.y, AccountPublished, ml);
            if (origins.Count == 0)
                origins.Add(entry);
            else
                origins[0] = entry;
        }
        AccountWrites++;
        BytesWritten += SparseEntryBytes;
        return SparseEntryBytes;
    }

    // Any machine: the account the host last published, in litres.
    public static bool TryAccount(FluidSystem? system, out double litres)
    {
        litres = 0;
        var origins = system?.Origins;
        if (origins == null || origins.Count == 0)
            return false;
        var entry = origins[0];
        if (entry.z != AccountPublished)
            return false;
        litres = entry.w / 1000.0;
        return true;
    }

    public int AccountWrites { get; private set; }

    public double TotalLitres()
    {
        double total = 0;
        float size = CellSize;
        foreach (var cell in System.Cells)
            total += FluidCell.Litres(cell, size);
        return total;
    }

    public double LitresIn(float3 localCentre, float radius)
    {
        double total = 0;
        float size = CellSize;
        float r2 = radius * radius;
        foreach (var cell in System.Cells)
        {
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                continue;
            if (float3.DistanceSquared(FluidCell.Centre(cell, size), localCentre) <= r2)
                total += FluidCell.Litres(cell, size);
        }
        return total;
    }

    // Litres inside a shape on a slot (a sensor's), from cell centres.
    public double LitresIn(Slot space, FluidShape shape, float3 size)
    {
        if (space == null || System.Slot == null)
            return 0;
        double total = 0;
        float cellSize = CellSize;
        foreach (var cell in System.Cells)
        {
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                continue;
            var global = System.Slot.LocalPointToGlobal(FluidCell.Centre(cell, cellSize));
            if (FluidSpawnFill.Inside(shape, size, space.GlobalPointToLocal(global)))
                total += FluidCell.Litres(cell, cellSize);
        }
        return total;
    }

    // A keeper bins each origin separately even when a cell is mixed. Keep those amounts beside the origin rather
    // than charging every mixed cell to its largest share. Existing origins without a sample still use cells. -xlinka
    public void PublishOrigin(int origin, double litres)
    {
        var origins = System.Origins;
        if (!World.IsAuthority || origin < 0 || origin >= origins.Count || !double.IsFinite(litres))
            return;
        var entry = origins[origin];
        int ml = (int)Math.Clamp(Math.Round(litres * 1000.0), 0, int.MaxValue);
        // The world's origin has no user or pour number, so its spare words hold its measured amount; w remains
        // the conserved whole-system account. A disappearing pour must not become authored fluid. -xlinka
        if (origin == 0)
        {
            if (entry.y == OriginMeasured && entry.x == ml)
                return;
            using (World.DataModelPermissions?.EnterSystemBypass())
                origins[0] = new int4(ml, OriginMeasured, entry.z, entry.w);
            BytesWritten += SparseEntryBytes;
            return;
        }
        if ((entry.z & OriginMeasured) != 0 && entry.w == ml)
            return;
        using (World.DataModelPermissions?.EnterSystemBypass())
            origins[origin] = new int4(entry.x, entry.y, entry.z | OriginMeasured, ml);
        BytesWritten += SparseEntryBytes;
    }

    // Litres per origin index, stale origins counted as the world's.
    public void OriginLitres(Dictionary<int, double> output)
    {
        output.Clear();
        float size = CellSize;
        foreach (var cell in System.Cells)
        {
            int fill = FluidCell.Fill(cell);
            if (fill == 0)
                continue;
            int origin = OriginOf(cell);
            output[origin] = output.GetValueOrDefault(origin) + FluidCell.Litres(cell, size);
        }
        double total = 0;
        foreach (double litres in output.Values)
            total += litres;
        double measured = 0;
        if (System.Origins.Count > 0 && System.Origins[0].y == OriginMeasured)
            output[0] = Math.Max(0, System.Origins[0].x) * 0.001;
        measured += output.GetValueOrDefault(0);
        for (int origin = 1; origin < System.Origins.Count; origin++)
        {
            var entry = System.Origins[origin];
            if (!_staleOrigins.Contains(origin) && (entry.z & OriginMeasured) != 0)
                output[origin] = Math.Max(0, entry.w) * 0.001;
            measured += output.GetValueOrDefault(origin);
        }
        // Reports and cells arrive over several frames; attribution cannot mint extra volume while they catch up.
        // Each origin returns to its measured share as the matching cells land. -xlinka
        if (measured > total && measured > 0)
            for (int origin = 0; origin < System.Origins.Count; origin++)
                if (output.TryGetValue(origin, out double litres))
                    output[origin] = litres * total / measured;
    }

    public int OriginOf(int4 cell)
    {
        int origin = FluidCell.Origin(cell);
        return _staleOrigins.Contains(origin) ? 0 : origin;
    }

    // ORIGINS (host)

    // Starts the origins over for this session: index 0 is the world, and whatever the loaded cells carry is stale.
    public void ResetOrigins()
    {
        if (_originsReset)
            return;
        _originsReset = true;
        _staleOrigins.Clear();
        foreach (var cell in System.Cells)
        {
            int origin = FluidCell.Origin(cell);
            if (origin != 0 && FluidCell.Fill(cell) > 0)
                _staleOrigins.Add(origin);
        }
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            var origins = System.Origins;
            // the world's entry keeps the account
            var first = origins.Count > 0 && origins[0].z == AccountPublished ? origins[0] : default;
            if (origins.Count != 1 || origins[0] != first)
            {
                origins.Clear();
                origins.Add(first);
            }
        }
    }

    public int OriginCount => System.Origins.Count;

    public int4 OriginEntry(int index) => index > 0 && index < System.Origins.Count ? System.Origins[index] : default;

    public static ulong OriginUser(int4 entry) => FluidRings.Join(entry.x, entry.y);

    public static int OriginNumber(int4 entry) => entry.z & 0xFFFFFF;

    public static bool OriginIsScript(int4 entry) => (entry.z & OriginScript) != 0;

    // Any machine: the origin a pour's particles carry, read off the replicated table. -1 until the host made it.
    public static int OriginIn(FluidSystem system, ulong user, int number)
    {
        var origins = system.Origins;
        for (int i = 1; i < origins.Count; i++)
        {
            var e = origins[i];
            if (OriginUser(e) == user && OriginNumber(e) == (number & 0xFFFFFF))
                return i;
        }
        return -1;
    }

    public int FindOrigin(ulong user, int number)
    {
        var origins = System.Origins;
        for (int i = 1; i < origins.Count; i++)
        {
            var e = origins[i];
            if (OriginUser(e) == user && OriginNumber(e) == (number & 0xFFFFFF) && !_staleOrigins.Contains(i))
                return i;
        }
        return -1;
    }

    // The origin index for a pour, made on first use. 0 (the world) when the table is full.
    public int OriginFor(ulong user, int number, bool script = false)
    {
        ResetOrigins();
        int found = FindOrigin(user, number);
        if (found > 0)
            return found;
        var origins = System.Origins;
        var entry = new int4(FluidRings.Low(user), FluidRings.High(user), (number & 0xFFFFFF) | (script ? OriginScript : 0), 0);
        using (World.DataModelPermissions?.EnterSystemBypass())
        {
            for (int i = 1; i < origins.Count; i++)
            {
                if (origins[i] == default && !_staleOrigins.Contains(i) && !InUse(i))
                {
                    origins[i] = entry;
                    return i;
                }
            }
            if (origins.Count >= MaxOrigins)
                return 0;
            while (_staleOrigins.Contains(origins.Count))
                origins.Add(default);
            if (origins.Count >= MaxOrigins)
                return 0;
            origins.Add(entry);
            return origins.Count - 1;
        }
    }

    private bool InUse(int origin)
    {
        foreach (var cell in System.Cells)
            if (FluidCell.Fill(cell) > 0 && FluidCell.Origin(cell) == origin)
                return true;
        return false;
    }

    public void MarkOriginFading(int origin)
    {
        var origins = System.Origins;
        if (origin <= 0 || origin >= origins.Count)
            return;
        var e = origins[origin];
        if ((e.z & OriginFading) != 0)
            return;
        using (World.DataModelPermissions?.EnterSystemBypass())
            origins[origin] = new int4(e.x, e.y, e.z | OriginFading, e.w);
    }
}
