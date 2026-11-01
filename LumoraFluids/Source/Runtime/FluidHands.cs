// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Components.Interaction;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Scoop, let go, throw, fling and wipe with bare hands, on the wearer's machine. It reads each local HandTool's grip
// edge in the fluid pass, after every grab has resolved, and leaves a hand alone while its Grabber holds something:
// a grip that closed on a prop is the prop's. No edit to Grabber or HandTool.
//
//   Scoop:    grip closes with the palm in fluid. What the hand holds is published on Held, and a Pickup goes out for
//             what left the world.
//   Let go:   grip opens: Drop, or Throw when the hand moves faster than 1.5 m/s.
//   Fling:    a held hand shaken past 30 m/s2.
//   Wipe:     a hand sweeping faster than 0.2 m/s over your own coated part takes coat off it.
//
// The desktop's virtual hands are HandTools too, so the grab key scoops there the same way. -xlinka
public sealed class FluidHands
{
    public const float ThrowSpeed = 1.5f;
    public const float FlingAcceleration = 30f;
    public const float WipeSpeed = 0.2f;
    // m, how close a sweeping palm must pass a spot
    public const float WipeReach = 0.06f;
    // share of a part's coat one 0.1 s of sweeping takes
    public const float WipePerTenth = 0.25f;
    public const float CapacityMl = 150f;
    public const double ToolScanGap = 1.0;

    private readonly World _world;
    private readonly FluidResidueBaker _baker;
    private readonly FluidPersonPublisher _publisher;
    private readonly List<HandTool> _tools = new();
    private readonly bool[] _grip = new bool[2];
    private readonly bool[] _hasLast = new bool[2];
    private readonly float3[] _lastPosition = new float3[2];
    private readonly float3[] _lastVelocity = new float3[2];
    private readonly float[] _heldMl = new float[2];
    private readonly uint[] _heldTint = new uint[2];
    private readonly int[] _heldTouched = new int[2];
    private readonly int[] _heldSubstance = new int[2];
    private readonly int[] _gripCount = new int[2];
    private double _nextScan;
    private int _seed;

    public FluidHands(World world, FluidResidueBaker baker, FluidPersonPublisher publisher)
    {
        _world = world;
        _baker = baker;
        _publisher = publisher;
    }

    // mL within reach of a palm this frame (by FluidBodyParts hand index), with the Touched index of its system, its
    // substance index there and its tint. Set by the space from the GPU's local query; nothing scoops without it.
    public Func<int, (float ml, int touched, int substance, uint tint, int cell)>? Scoopable { get; set; }

    public bool AllowScooping { get; set; } = true;

    public float HeldMl(int side) => side is 0 or 1 ? _heldMl[side] : 0f;

    public void Update(FluidPerson? person, float dt)
    {
        if (person == null || !person.IsLocalWearer || !person.HasFixedLengths || !(dt > 1e-5f))
            return;
        RefreshTools();
        foreach (var tool in _tools)
        {
            if (tool == null || tool.IsDestroyed || tool.Slot == null)
                continue;
            int side = tool.Side.Value == Chirality.Right ? 1 : 0;
            int part = side == 0 ? FluidBodyParts.LeftHand : FluidBodyParts.RightHand;
            var position = tool.Slot.GlobalPosition;
            var velocity = _hasLast[side] ? (position - _lastPosition[side]) / dt : float3.Zero;
            var acceleration = _hasLast[side] ? (velocity - _lastVelocity[side]) / dt : float3.Zero;
            _lastPosition[side] = position;
            _lastVelocity[side] = velocity;
            _hasLast[side] = true;

            bool grip = tool.GripHeld;
            bool wasGrip = _grip[side];
            _grip[side] = grip;
            var grabber = tool.Grabber;
            bool busy = grabber == null || grabber.IsDestroyed || grabber.IsHoldingObjects;

            if (grip && !wasGrip && !busy)
                Scoop(person, side, part, position);
            else if (!grip && wasGrip && _heldMl[side] > 0f)
                LetGo(person, side, part, position, velocity, velocity.Length > ThrowSpeed ? FluidReleaseKind.Throw : FluidReleaseKind.Drop);
            else if (grip && _heldMl[side] > 0f && acceleration.Length > FlingAcceleration)
                LetGo(person, side, part, position, velocity, FluidReleaseKind.Fling);

            if (!grip && velocity.Length > WipeSpeed)
            {
                int bits = _baker.PartsNear(position, WipeReach, part);
                for (int p = 0; p < FluidBodyParts.Count && bits != 0; p++)
                {
                    if ((bits & FluidBodyParts.Bit(p)) == 0)
                        continue;
                    bits &= ~FluidBodyParts.Bit(p);
                    _baker.Wipe(p, part, MathF.Min(0.5f, WipePerTenth * dt / 0.1f));
                }
            }
        }
    }

    private void Scoop(FluidPerson person, int side, int part, float3 position)
    {
        if (!AllowScooping || _baker.Mode == FluidStickMode.Off || (_publisher.TouchBits & FluidBodyParts.Bit(part)) == 0 || Scoopable == null)
            return;
        var (ml, touched, substance, tint, cell) = Scoopable(part);
        ml = MathF.Min(ml, CapacityMl - _heldMl[side]);
        if (!(ml > 0f))
            return;
        _heldMl[side] += ml;
        _heldTint[side] = tint;
        _heldTouched[side] = touched;
        _heldSubstance[side] = substance;
        _gripCount[side]++;
        PublishHeld(person, side);
        // The actual cell lets the host validate the pickup and the keeper remove exactly that share. -xlinka
        person.WriteRelease(new FluidRelease(FluidReleaseKind.Pickup, position, float3.Zero, ml * 1e-3f, FluidBodyParts.Nodes[part],
            substance, ++_seed, touched, cell));
    }

    private void LetGo(FluidPerson person, int side, int part, float3 position, float3 velocity, FluidReleaseKind kind)
    {
        float ml = _heldMl[side];
        _heldMl[side] = 0f;
        PublishHeld(person, side);
        person.WriteRelease(new FluidRelease(kind, position, kind == FluidReleaseKind.Drop ? float3.Zero : velocity, ml * 1e-3f,
            FluidBodyParts.Nodes[part], _heldSubstance[side], ++_seed, _heldTouched[side], FluidRelease.NoCell));
    }

    // Held, per hand: x = uL, y = tint RGBA8, z = Touched index 8 | substance 4 << 8, w = grip counter.
    private void PublishHeld(FluidPerson person, int side)
    {
        if (side >= person.Held.Count)
            return;
        var value = new int4((int)MathF.Round(_heldMl[side] * 1000f), unchecked((int)_heldTint[side]),
            (_heldTouched[side] & 0xFF) | ((_heldSubstance[side] & 0xF) << 8), _gripCount[side]);
        if (person.Held[side] != value)
            person.Held[side] = value;
    }

    private void RefreshTools()
    {
        double now = _world.Time.TotalTime;
        if (now < _nextScan)
            return;
        _nextScan = now + ToolScanGap;
        _tools.Clear();
        var root = _world.LocalUser?.Root?.Slot;
        if (root == null || root.IsDestroyed)
            return;
        foreach (var tool in root.GetComponentsInChildren<HandTool>())
            _tools.Add(tool);
    }
}
