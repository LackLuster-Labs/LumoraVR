// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Pending remote volume and the Pickups that claim it, on the keeper; the host runs the same acceptance rule on the
// same replicated ring, so both agree on every Pickup without saying so.
//
// A particle the keeper's simulation anchored to a remote person still counts in its home cell, as pending volume for
// that person, until their own machine decides. Their Pickup claims the pending volume first, then the cell's fill,
// clamped to what the cell holds; the keeper then takes that much out of its simulation, so its next bins show it.
// Pending volume nobody claims within 10 s simply stays in the world. A Pickup from a cell more than 1 m from the
// wearer's body is dropped, and a person's Pickups are capped at CoatLimit per body part, plus two hands' worth, per
// 10 s. Goo the keeper's own body picks up is its own Pickup, applied directly.
//
// Releases go the other way, and every machine clamps one to what the person carries: the coat and held goo they last
// published, plus the Pickups since, minus what they already released since. The wearer publishes spots with or after
// the releases they cover, so the clamp reads the larger of the coat before this frame and the coat now, and only
// starts over once the published coat has moved. Nobody makes goo out of nothing. -xlinka
public sealed class FluidPickupBook
{
    public const double MaxDistance = 1.0;
    public const double Window = 10.0;
    public const double ClaimWindow = 10.0;
    // L a hand may scoop per window on top of the coat limit
    public const double HandScoop = 0.25;

    private readonly Dictionary<(User, int), List<(double At, double Litres)>> _recent = new();
    private readonly Dictionary<(FluidSystem, int, byte), (double Litres, double Since)> _pending = new();
    private readonly Dictionary<(FluidSystem, int), double> _claimed = new();
    private readonly Dictionary<User, (double Coat, double Picked, double Released)> _carried = new();
    private double _claimedAt = double.NaN;

    public int Accepted { get; private set; }

    public int Dropped { get; private set; }

    public void NotePending(FluidSystem system, int cell, byte person, double litres, double now)
    {
        var key = (system, cell, person);
        var (had, since) = _pending.GetValueOrDefault(key, (0, now));
        _pending[key] = (litres, had > 0 ? since : now);
    }

    public void Expire(double now)
    {
        List<(FluidSystem, int, byte)>? old = null;
        foreach (var (key, value) in _pending)
            if (now - value.Since > ClaimWindow)
                (old ??= new()).Add(key);
        if (old != null)
            foreach (var key in old)
                _pending.Remove(key);
    }

    public double PendingFor(FluidSystem system, int cell, byte person) => _pending.GetValueOrDefault((system, cell, person)).Litres;

    // How much of a Pickup stands. Zero drops it.
    public double Accept(User wearer, in FluidRelease release, FluidSystem system, float coatLimitMillilitres, double now, out string why)
    {
        why = string.Empty;
        if (release.Kind != FluidReleaseKind.Pickup || wearer == null || system == null || system.Slot == null)
        {
            why = "not a Pickup";
            return Drop();
        }
        if (_claimedAt != now)
        {
            _claimed.Clear();
            _claimedAt = now;
        }
        var cells = system.Cells;
        bool bakedHere = release.Cell == FluidRelease.NoCell && ReferenceEquals(system.Keeper.Target, wearer);
        if (!bakedHere && (release.Cell < 0 || release.Cell >= cells.Count))
        {
            why = "no such cell";
            return Drop();
        }
        var cell = bakedHere ? default : cells[release.Cell];
        float size = FluidLedger.CellSizeOf(system);
        if (!bakedHere && FluidCell.Anchor(cell) == FluidCell.NoAnchor)
        {
            var centre = system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
            if (DistanceToBody(wearer, centre) > MaxDistance + size)
            {
                why = "the cell is out of reach";
                return Drop();
            }
        }

        var key = (wearer, (int)release.Part);
        if (!_recent.TryGetValue(key, out var recent))
            _recent[key] = recent = new List<(double, double)>();
        recent.RemoveAll(r => now - r.At > Window);
        double used = 0;
        foreach (var r in recent)
            used += r.Litres;
        double cap = coatLimitMillilitres * 1e-3 + (IsHand(release.Part) ? HandScoop : 0);
        double litres = Math.Min(Math.Max(0, release.Litres), Math.Max(0, cap - used));

        byte person = wearer.AllocationID.Value;
        double pending = PendingFor(system, release.Cell, person);
        double available = pending + FluidCell.Litres(cell, size);
        // NoCell is emitted only for the keeper's own bake, after its exact particle removal. It still debits the
        // host's account, otherwise reconciliation grows the coat's volume back into the world. -xlinka
        if (bakedHere)
            foreach (var value in cells)
                available += FluidCell.Litres(value, size);
        available -= _claimed.GetValueOrDefault((system, release.Cell));
        litres = Math.Min(litres, Math.Max(0, available));
        if (litres <= 0)
        {
            why = "nothing left to claim";
            return Drop();
        }
        if (pending > 0)
            _pending[(system, release.Cell, person)] = (Math.Max(0, pending - litres), now);
        _claimed[(system, release.Cell)] = _claimed.GetValueOrDefault((system, release.Cell)) + litres;
        recent.Add((now, litres));
        Accepted++;
        if (FluidProvenance.Of(wearer) is { } record)
            NotePickup(wearer, record, litres);
        return litres;
    }

    // How much of a release off a person stands, on any machine. Call Settle for the person after the frame's releases.
    public double ClampRelease(User wearer, FluidPerson person, double litres)
    {
        if (wearer == null || person == null || litres <= 0)
            return 0;
        double coat = FluidLedgerWorld.CoatOf(person);
        var (before, picked, released) = _carried.GetValueOrDefault(wearer, (coat, 0, 0));
        double available = Math.Max(before, coat) + picked - released;
        double take = Math.Min(litres, Math.Max(0, available));
        _carried[wearer] = (before, picked, released + take);
        return take;
    }

    public void NotePickup(User wearer, FluidPerson person, double litres)
    {
        if (wearer == null || person == null || litres <= 0)
            return;
        double coat = FluidLedgerWorld.CoatOf(person);
        var (before, picked, released) = _carried.GetValueOrDefault(wearer, (coat, 0, 0));
        _carried[wearer] = (before, picked + litres, released);
    }

    // After a frame's ring entries: once the published coat moved, it covers what came before.
    public void Settle(User wearer, FluidPerson person)
    {
        if (wearer == null || person == null)
            return;
        double coat = FluidLedgerWorld.CoatOf(person);
        if (!_carried.TryGetValue(wearer, out var state))
            _carried[wearer] = (coat, 0, 0);
        else if (Math.Abs(state.Coat - coat) > 1e-9)
            _carried[wearer] = (coat, 0, 0);
    }

    public void Forget(User wearer)
    {
        _carried.Remove(wearer);
        _recent.Remove((wearer, (int)BodyNode.LeftHand));
        _recent.Remove((wearer, (int)BodyNode.RightHand));
    }

    // The keeper's side of an accepted Pickup: take it out of the local simulation, from that cell.
    public static FluidCommand Removal(FluidSystem system, int cellIndex, double litres)
    {
        float size = FluidLedger.CellSizeOf(system);
        var cell = cellIndex >= 0 && cellIndex < system.Cells.Count ? system.Cells[cellIndex] : default;
        return new FluidCommand(FluidCommandKind.Remove, FluidCommand.AnyOrigin, FluidCell.Centre(cell, size), size * 0.87f, (float)litres, 0, cellIndex);
    }

    public static bool IsHand(BodyNode part)
        => part is BodyNode.LeftHand or BodyNode.RightHand or BodyNode.LeftController or BodyNode.RightController;

    // The nearest of the body's line from feet to head.
    public static float DistanceToBody(User user, float3 point)
    {
        var root = user?.Root;
        if (root?.Slot == null)
            return float.MaxValue;
        var feet = root.Slot.GlobalPosition;
        var head = root.HeadPosition;
        var axis = head - feet;
        float length2 = axis.LengthSquared;
        float t = length2 > 1e-6f ? Math.Clamp(float3.Dot(point - feet, axis) / length2, 0f, 1f) : 0f;
        return float3.Distance(point, feet + axis * t);
    }

    private double Drop()
    {
        Dropped++;
        return 0;
    }
}
