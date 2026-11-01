// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The optional feel and two-way body response live beside the solver, through its existing world events. -xlinka
public sealed class FluidCoupling
{
    private static readonly ConditionalWeakTable<World, FluidCoupling> Worlds = new();
    private static bool _registered;
    private readonly FluidSpace _space;
    private readonly FluidPeopleLink _people;
    private readonly float[] _held = new float[2];
    private readonly FluidSubstance?[] _lastSubstance = new FluidSubstance?[FluidBodyParts.Count];
    private int _touchBits;

    private FluidCoupling(FluidSpace space)
    {
        _space = space;
        _people = FluidPeopleLink.Attach(space)!;
        Impulses = new FluidImpulses(space);
        SoftBodies = new FluidSoftBodyGather(space);
        Haptics = new FluidHaptics(space);
        Sounds = new FluidSounds(space);
        HandQueries = new FluidHandQueries(space, _people);
        _people.Publisher.Snapped += OnSnap;
        space.SummaryArrived += OnSummary;
        space.Updating += OnUpdating;
        space.Packing += OnPacking;
        space.Destroyed += OnDestroyed;
        space.LocalRootChanged += OnRootChanged;
    }

    public FluidImpulses Impulses { get; }
    public FluidFooting Footing { get; } = new();
    public FluidSoftBodyGather SoftBodies { get; }
    public FluidHaptics Haptics { get; }
    public FluidSounds Sounds { get; }
    public FluidHandQueries HandQueries { get; }

    public static FluidCoupling? Find(World? world) => world != null && Worlds.TryGetValue(world, out var value) ? value : null;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        FluidSpace.Created += Attach;
    }

    public static void Attach(FluidSpace space)
    {
        if (!space.IsDestroyed && !Worlds.TryGetValue(space.World, out _))
            Worlds.Add(space.World, new FluidCoupling(space));
    }

    public void UpdateFooting(float delta)
    {
        var person = _space.LocalPerson;
        var rules = _space.World.RootSlot.GetComponent<FluidWorldRules>();
        bool allowed = _space.Focused && person is { IsLocalWearer: true } && person.Stick.Value == FluidStickMode.Sticky
            && (rules == null || (rules.SlowPeople.Value && rules.StickToPeople.Value));
        var publisher = _people.Publisher;
        float depth = 0f, traction = 1f, grip = 0f, stuck = 0f;
        bool touching = false;
        for (int part = FluidBodyParts.LeftFoot; part <= FluidBodyParts.RightLowerLeg; part++)
        {
            if ((publisher.TouchBits & FluidBodyParts.Bit(part)) == 0)
                continue;
            touching = true;
            depth = MathF.Max(depth, publisher.Depth[part] / 0.03f);
            var substance = SubstanceForPart(part);
            if (substance != null)
            {
                traction = MathF.Min(traction, substance.Traction.Value);
                grip = MathF.Max(grip, substance.FootGrip.Value);
            }
            stuck = MathF.Max(stuck, publisher.Stuck[FluidBodyParts.LimbOf(part)]);
            if (_space.ViewOnly)
                depth = MathF.Max(depth, stuck);
        }
        Footing.Update(delta, allowed, depth, traction, grip, stuck, touching);
    }

    private void OnSummary(FluidSpace space, FluidSummaryView view)
    {
        Impulses.TakeSummary(view);
        HandQueries.TakeSummary(view);
    }

    private void OnPacking(FluidSpace space, FluidFrameBlock block)
    {
        Impulses.Capture(block);
        SoftBodies.Gather(block);
        HandQueries.Pack(block);
    }

    private void OnUpdating(FluidSpace space)
    {
        var publisher = _people.Publisher;
        int touches = publisher.TouchBits;
        int entered = touches & ~_touchBits;
        _touchBits = touches;
        if (space.LocalPerson is not { IsLocalWearer: true } person || person.Stick.Value == FluidStickMode.Off)
            return;
        for (int part = 0; part < FluidBodyParts.Count; part++)
        {
            var substance = SubstanceForPart(part);
            if (substance != null && (touches & FluidBodyParts.Bit(part)) != 0)
                _lastSubstance[part] = substance;
            if ((entered & FluidBodyParts.Bit(part)) == 0)
                continue;
            if (part is FluidBodyParts.LeftHand or FluidBodyParts.RightHand)
                Haptics.Contact(part - FluidBodyParts.LeftHand, substance?.Adhesion.Value ?? 0f);
            Sounds.Play(substance?.SplashSound.Target, substance, Position(part), Math.Clamp(publisher.Speed[part], 0.15f, 1f));
        }
        for (int side = 0; side < 2; side++)
        {
            float held = _people.Hands.HeldMl(side);
            int part = FluidBodyParts.LeftHand + side;
            var substance = _lastSubstance[part];
            if (held > _held[side] + 0.01f)
            {
                Haptics.Scoop(side);
                Sounds.Play(substance?.SquelchSound.Target, substance, Position(part));
            }
            else if (held < _held[side] - 0.01f)
                Sounds.Play(substance?.DripSound.Target, substance, Position(part));
            _held[side] = held;
        }
    }

    private void OnSnap(int limb, float length)
    {
        if (_space.LocalPerson is not { IsLocalWearer: true } person || person.Stick.Value == FluidStickMode.Off)
            return;
        int part = FluidBodyParts.PartOfLimb(limb);
        if (part < 0)
            return;
        if (limb is FluidBodyParts.LimbLeftHand or FluidBodyParts.LimbRightHand)
            Haptics.Snap(limb - FluidBodyParts.LimbLeftHand);
        var substance = _lastSubstance[part];
        Sounds.Play(substance?.SnapSound.Target, substance, Position(part));
    }

    private float3 Position(int part) => _people.People.PartBone(_people.People.LocalSlot, part)?.GlobalPosition
        ?? _space.LocalRoot?.Slot.GlobalPosition ?? float3.Zero;

    // View-only contact reports carry no substance index; a matching coat supplies its authored walking feel. -xlinka
    private FluidSubstance? SubstanceForPart(int part)
    {
        var substance = SubstanceAt(_people.Publisher.Substance[part]);
        if (substance != null || !_space.ViewOnly || _space.LocalPerson is not { } person)
            return substance;
        float largest = 0f;
        for (int i = 0; i < person.Spots.Count; i++)
        {
            var spot = person.ReadSpot(i);
            if (spot.IsEmpty || spot.Node != FluidBodyParts.Nodes[part] || spot.BirthMillilitres <= largest
                || spot.Touched < 0 || spot.Touched >= person.Touched.Count)
                continue;
            var id = FluidPerson.UnpackRef(person.Touched[spot.Touched]);
            if (_space.World.ReferenceController.GetObjectOrNull(id) is not FluidSystem system || system.IsDestroyed
                || spot.Substance < 0 || spot.Substance >= system.Substances.Count)
                continue;
            var candidate = system.Substances[spot.Substance];
            if (candidate == null || candidate.IsDestroyed)
                continue;
            largest = spot.BirthMillilitres;
            substance = candidate;
        }
        return substance;
    }

    internal FluidSubstance? SubstanceAt(int index)
    {
        if (index < 0)
            return null;
        foreach (var system in _space.Systems)
        {
            if (system.IsDestroyed)
                continue;
            foreach (var substance in system.Substances)
            {
                if (substance == null || substance.IsDestroyed)
                    continue;
                if (index-- == 0)
                    return substance;
            }
        }
        return null;
    }

    private void OnRootChanged(FluidSpace space)
    {
        Footing.Restore();
        Footing.Update(0f, false, 0f, 1f, 0f, 0f, false);
        _touchBits = 0;
        HandQueries.Reset();
        Array.Clear(_held);
        Array.Clear(_lastSubstance);
    }

    private void OnDestroyed(FluidSpace space)
    {
        Footing.Restore();
        _people.Publisher.Snapped -= OnSnap;
        space.SummaryArrived -= OnSummary;
        space.Updating -= OnUpdating;
        space.Packing -= OnPacking;
        space.Destroyed -= OnDestroyed;
        space.LocalRootChanged -= OnRootChanged;
        Worlds.Remove(space.World);
    }
}
