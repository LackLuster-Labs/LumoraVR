// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A scoop needs measured fluid at the palm, not an estimate from the hand's contact depth. -xlinka
public sealed class FluidHandQueries
{
    public const float Radius = 0.06f;
    private readonly FluidSpace _space;
    private readonly FluidPeopleLink _people;
    private readonly Dictionary<uint, (int Left, int Right, float3 LeftAt, float3 RightAt)> _frames = new();
    private readonly Queue<uint> _order = new();
    private readonly float[] _ml = new float[2];
    private readonly float3[] _at = new float3[2];
    private double _received = double.NegativeInfinity;
    private readonly Dictionary<(FluidSystem System, int Cell), (int4 Before, double Taken)> _claimed = new();

    public FluidHandQueries(FluidSpace space, FluidPeopleLink people)
    {
        _space = space;
        _people = people;
        people.Hands.Scoopable = Take;
    }

    public void Pack(FluidFrameBlock block)
    {
        int left = Add(block, FluidBodyParts.LeftHand, out var leftAt);
        int right = Add(block, FluidBodyParts.RightHand, out var rightAt);
        uint sequence = unchecked((uint)block.Sequence + 1);
        if (!_frames.ContainsKey(sequence))
            _order.Enqueue(sequence);
        _frames[sequence] = (left, right, leftAt, rightAt);
        while (_order.Count > 32)
            _frames.Remove(_order.Dequeue());
    }

    private int Add(FluidFrameBlock block, int part, out float3 at)
    {
        at = default;
        var bone = Bone(part);
        if (bone == null || bone.IsDestroyed || block.Queries.Count >= FluidLayout.SummaryQueriesCapacity)
            return -1;
        at = bone.GlobalPosition;
        int index = block.Queries.Count;
        block.Queries.Add(new FluidQuerySphere { Center = at, Radius = Radius });
        return index;
    }

    public void TakeSummary(in FluidSummaryView view)
    {
        if (!_frames.TryGetValue(view.Header.SubmittedSequence, out var frame))
            return;
        _received = _space.World.Time.TotalTime;
        var queries = view.Queries;
        _ml[0] = frame.Left >= 0 && frame.Left < queries.Length ? queries[frame.Left].LocalLitres * 1000f : 0f;
        _ml[1] = frame.Right >= 0 && frame.Right < queries.Length ? queries[frame.Right].LocalLitres * 1000f : 0f;
        _at[0] = frame.LeftAt;
        _at[1] = frame.RightAt;
    }

    private (float ml, int touched, int substance, uint tint, int cell) Take(int part)
    {
        int side = part - FluidBodyParts.LeftHand;
        if (side is not (0 or 1) || !_space.Focused)
            return default;
        var bone = Bone(part);
        if (bone == null || bone.IsDestroyed)
            return default;
        bool viewOnly = _space.ViewOnly;
        if (!viewOnly && (_space.World.Time.TotalTime - _received > 0.15
            || float3.DistanceSquared(bone.GlobalPosition, _at[side]) > Radius * Radius))
            return default;
        float available = viewOnly ? FluidHands.CapacityMl : _ml[side];
        if (!float.IsFinite(available) || !(available > 0f))
            return default;
        var preferred = FluidCoupling.Find(_space.World)?.SubstanceAt(_people.Publisher.Substance[part]);
        FluidSystem? picked = null;
        FluidSubstance? substance = null;
        int cellIndex = -1;
        double most = 0;
        foreach (var system in _space.Systems)
        {
            if (system.IsDestroyed || !system.Enabled.Value)
                continue;
            float size = system.EffectiveCellSize;
            for (int i = 0; i < system.Cells.Count; i++)
            {
                var cell = system.Cells[i];
                int kind = FluidCell.Substance(cell);
                if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor || kind >= system.Substances.Count)
                    continue;
                var candidate = system.Substances[kind];
                if (candidate == null || candidate.IsDestroyed || (preferred != null && !ReferenceEquals(candidate, preferred)))
                    continue;
                var point = system.Slot.LocalPointToGlobal(FluidCell.Centre(cell, size));
                if (float3.DistanceSquared(point, bone.GlobalPosition) > Radius * Radius)
                    continue;
                double ml = FluidCell.Litres(cell, size) * 1000.0;
                if (_claimed.TryGetValue((system, i), out var claim))
                {
                    if (FluidCell.SameKey(cell, claim.Before) && FluidCell.SameKind(cell, claim.Before))
                    {
                        double removed = Math.Max(0, (FluidCell.Litres(claim.Before, size) - FluidCell.Litres(cell, size)) * 1000.0);
                        claim = (cell, Math.Max(0, claim.Taken - removed));
                        _claimed[(system, i)] = claim;
                        ml -= claim.Taken;
                    }
                    else
                        _claimed.Remove((system, i));
                }
                if (ml <= most)
                    continue;
                most = ml;
                picked = system;
                substance = candidate;
                cellIndex = i;
            }
        }
        if (picked == null || substance == null || !_people.Baker.PreparePickup(substance, out int touched, out int inSystem))
            return default;
        float take = MathF.Min(MathF.Min(available, (float)most), FluidHands.CapacityMl - _people.Hands.HeldMl(side));
        // Held volumes are whole microlitres, so a fractional remainder cannot fund another grip. -xlinka
        take = MathF.Floor(take * 1000f) * 0.001f;
        if (!(take > 0f))
            return default;
        // Both palms share the same pending debit, so overlapping queries cannot spend a stale ledger twice. -xlinka
        var key = (picked, cellIndex);
        double spent = _claimed.GetValueOrDefault(key).Taken;
        _claimed[key] = (picked.Cells[cellIndex], spent + take);
        _ml[side] = 0f;
        var tint = substance.Tint.Value;
        uint rgba = Byte(tint.r) | (Byte(tint.g) << 8) | (Byte(tint.b) << 16) | (Byte(tint.a) << 24);
        return (take, touched, inSystem, rgba, cellIndex);
    }

    private Slot? Bone(int part) => _people.People.PartBone(_people.People.LocalSlot, part)
        ?? (part == FluidBodyParts.LeftHand ? _space.LocalRoot?.LeftHandSlot : _space.LocalRoot?.RightHandSlot);

    public void Reset()
    {
        Array.Clear(_ml);
        _frames.Clear();
        _claimed.Clear();
        _order.Clear();
        _received = double.NegativeInfinity;
    }

    private static uint Byte(float v) => float.IsFinite(v) ? (uint)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) : 0u;
}
