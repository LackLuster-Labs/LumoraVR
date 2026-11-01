// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lumora.Core;
using Lumora.Core.Math;
using LumoraLogger = Lumora.Core.Logging.Logger;

namespace Lumora.Fluids;

// The people side of a world's fluid, run through its space's events: the gather packs everyone near fluid into each
// frame block, the wearer's summaries bake into their coat, the publisher and the hands keep FluidPerson current, and
// a machine that does not simulate still gathers the coats in view for the residue renderer. One per space, attached
// the first time the space attaches the wearer's FluidPerson, and gone with the space.
//
// Order inside a world's frame: the space's update takes the summaries (SummaryArrived: contacts, limbs and bakes),
// then raises Updating (the coat, the members, the hands, and the view-only residue); its late update packs the block
// and raises Packing (residue, people, wake boxes, and the particles the bakes took). -xlinka
public sealed class FluidPeopleLink
{
    // Particle ids one RemoveParticles command carries (A, B and C, four each).
    public const int IdsPerCommand = 12;
    // m around a system's slot that a system with no particles yet still gathers people in
    public const float EmptySystemReach = 1f;

    private static readonly ConditionalWeakTable<FluidSpace, FluidPeopleLink> BySpace = new();
    private static readonly ConditionalWeakTable<World, FluidPeopleLink> ByWorld = new();

    private readonly FluidSpace _space;
    private bool _failed;

    private FluidPeopleLink(FluidSpace space)
    {
        _space = space;
        var world = space.World;
        People = new FluidPeopleGather(world);
        Baker = new FluidResidueBaker(world) { FrameSubstance = FrameSubstance };
        Publisher = new FluidPersonPublisher(world, Baker);
        Hands = new FluidHands(world, Baker, Publisher);
        Coats = new FluidCoatRelay(world, People, Baker, Publisher, FrameSubstance);
        space.SummaryArrived += OnSummary;
        space.Updating += OnUpdating;
        space.Packing += OnPacking;
        space.Destroyed += OnDestroyed;
    }

    public FluidPeopleGather People { get; }

    public FluidResidueBaker Baker { get; }

    public FluidPersonPublisher Publisher { get; }

    public FluidHands Hands { get; }

    // Coats for view-only people: proposed from this machine's GPU when it keeps, applied when this wearer is view only.
    public FluidCoatRelay Coats { get; }

    // On a machine that does not simulate: the coats in view this update, in the residue renderer's records. On one
    // that simulates they go into the frame block instead. Version moves each time either is gathered.
    public FluidFrameBlock Residue { get; } = new();

    public int ResidueVersion { get; private set; }

    public int Summaries { get; private set; }

    public int Baked { get; private set; }

    public int RemovesSent { get; private set; }

    public int WakeBoxes { get; private set; }

    // Wet patches that changed the wearer's coat.
    public int WetPatches { get; private set; }

    private readonly List<FluidWetSample> _wet = new();

    // Raised on the world's thread each frame with that frame's coats in view, in the residue renderer's records
    // (block.Spots and block.ResidueBones; the gather's ResidueMin and ResidueMax bound them). The residue renderer's
    // hook takes them with SetSpots: this assembly draws nothing itself.
    public static event Action<World, FluidFrameBlock>? ResidueGathered;

    public static FluidPeopleLink? Find(World? world) => world != null && ByWorld.TryGetValue(world, out var link) ? link : null;

    // Safe to call again: a space gets one.
    public static FluidPeopleLink? Attach(FluidSpace? space)
    {
        if (space == null || space.IsDestroyed)
            return null;
        lock (BySpace)
        {
            if (BySpace.TryGetValue(space, out var found))
                return found;
            var link = new FluidPeopleLink(space);
            BySpace.Add(space, link);
            ByWorld.AddOrUpdate(space.World, link);
            return link;
        }
    }

    private void OnSummary(FluidSpace space, FluidSummaryView view)
    {
        Summaries++;
        try
        {
            int slot = People.LocalSlot;
            Publisher.TakeSummary(view, slot);
            Baked += Baker.Bake(view.Bakes, slot);
            Coats.OnSummary(view);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnUpdating(FluidSpace space)
    {
        try
        {
            var rules = space.World.RootSlot?.GetComponent<FluidWorldRules>();
            if (rules != null && !rules.IsDestroyed)
            {
                Baker.CoatLimitMl = rules.CoatLimit.Value;
                Baker.WetCapMl = rules.WetCap.Value;
                Hands.AllowScooping = rules.AllowScooping.Value;
            }
            Publisher.Update(FluidGpu.Current, space.Focused, space.Budget.Healthy);
            // Where fluid ran over the wearer's skin: water and the like leave their film.
            var wet = FluidSkinWet.Find(space.World);
            if (wet != null)
            {
                _wet.Clear();
                if (wet.TakeAll(_wet) > 0)
                {
                    WetPatches += Baker.Wet(_wet, People.LocalSlot);
                    Coats.OnWet(_wet);
                }
            }
            Coats.Update(space);
            Hands.Update(Publisher.EnsureAttached(), (float)space.World.Time.Delta);
            if (!space.Simulating)
            {
                People.Tier = space.Tier == FluidGpuTier.None ? FluidGpuTier.Low : space.Tier;
                Residue.BeginFrame();
                People.GatherResidue(Residue);
                ResidueVersion++;
                ResidueGathered?.Invoke(space.World, Residue);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnPacking(FluidSpace space, FluidFrameBlock block)
    {
        try
        {
            People.Tier = space.Tier;
            bool keeper = false;
            foreach (var system in space.Systems)
                keeper |= system != null && !system.IsDestroyed && system.IsKeeperHere;
            People.Keeper = keeper;
            People.GatherResidue(block);
            ResidueVersion++;
            ResidueGathered?.Invoke(space.World, block);
            if (Bounds(space, out var min, out var max))
                People.Gather(block, min, max);

            // Bones that moved wake the fluid around them.
            var moved = People.MovedBounds;
            for (int i = 0; i + 1 < moved.Count; i += 2)
            {
                var lo = moved[i];
                var hi = moved[i + 1];
                block.Commands.Add(new FluidGpuCommand
                {
                    Op = (uint)FluidGpuOp.WakeBox,
                    System = FluidLayout.None,
                    A = new float4((lo + hi) * 0.5f, 0f),
                    B = new float4((hi - lo) * 0.5f, 0f),
                });
                WakeBoxes++;
            }

            // What the bakes took leaves the simulation in the same frame the coat got it, and so does what a view-only
            // wearer's Pickup claimed of the coats this machine proposed to them.
            var removes = Baker.PendingRemoves;
            if (Coats.PendingRemoves.Count > 0)
            {
                removes.AddRange(Coats.PendingRemoves);
                Coats.PendingRemoves.Clear();
            }
            Span<float> ids = stackalloc float[IdsPerCommand];
            for (int at = 0; at < removes.Count; at += IdsPerCommand)
            {
                int n = Math.Min(IdsPerCommand, removes.Count - at);
                ids.Clear();
                for (int j = 0; j < n; j++)
                    ids[j] = BitConverter.UInt32BitsToSingle(removes[at + j]);
                block.Commands.Add(new FluidGpuCommand
                {
                    Op = (uint)FluidGpuOp.RemoveParticles,
                    System = FluidLayout.None,
                    Count = (uint)n,
                    A = new float4(ids[0], ids[1], ids[2], ids[3]),
                    B = new float4(ids[4], ids[5], ids[6], ids[7]),
                    C = new float4(ids[8], ids[9], ids[10], ids[11]),
                });
                RemovesSent += n;
            }
            removes.Clear();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnDestroyed(FluidSpace space)
    {
        space.SummaryArrived -= OnSummary;
        space.Updating -= OnUpdating;
        space.Packing -= OnPacking;
        space.Destroyed -= OnDestroyed;
        Publisher.Dispose();
        var block = space.Gpu?.Block;
        if (block != null)
            People.Clear(block);
        lock (BySpace)
        {
            BySpace.Remove(space);
            if (ByWorld.TryGetValue(space.World, out var link) && ReferenceEquals(link, this))
                ByWorld.Remove(space.World);
        }
    }

    // The union of every system's particles (the summary's bounds, asleep ones included) and, for a system with none
    // yet, the ground around its slot. The gather grows it by its own reach.
    private static bool Bounds(FluidSpace space, out float3 min, out float3 max)
    {
        min = new float3(float.MaxValue);
        max = new float3(float.MinValue);
        bool any = false;
        var status = space.Status;
        for (int i = 0; i < space.Systems.Count; i++)
        {
            var system = space.Systems[i];
            if (system == null || system.IsDestroyed || system.Slot == null)
                continue;
            float3 lo, hi;
            if (i < status.Length && status[i].BoundsMax.x >= status[i].BoundsMin.x && float.IsFinite(status[i].BoundsMin.x + status[i].BoundsMax.x)
                && (status[i].BoundsMax.x > status[i].BoundsMin.x || status[i].BoundsMax.y > status[i].BoundsMin.y))
            {
                // Bounds come in the system's frame's space; a container's frame is its slot.
                lo = status[i].BoundsMin;
                hi = status[i].BoundsMax;
                if (space.ContainerOf(system) != null)
                {
                    var a = system.Slot.LocalPointToGlobal(lo);
                    var b = system.Slot.LocalPointToGlobal(hi);
                    lo = Min(a, b);
                    hi = Max(a, b);
                }
            }
            else
            {
                var at = system.Slot.GlobalPosition;
                lo = at - new float3(EmptySystemReach);
                hi = at + new float3(EmptySystemReach);
            }
            min = Min(min, lo);
            max = Max(max, hi);
            any = true;
        }
        return any;
    }

    // A frame substance index (the order the packer lays substances out: systems in order, each system's live
    // substances in order) back to its component.
    private FluidSubstance? FrameSubstance(int index)
    {
        if (index < 0)
            return null;
        int at = 0;
        foreach (var system in _space.Systems)
        {
            if (system == null || system.IsDestroyed)
                continue;
            foreach (var substance in system.Substances)
            {
                if (substance == null || substance.IsDestroyed)
                    continue;
                if (at == index)
                    return substance;
                at++;
            }
        }
        return null;
    }

    // Another lane's state that throws must not take the world's fluid pass down; logged once.
    private void Fail(Exception ex)
    {
        if (_failed)
            return;
        _failed = true;
        LumoraLogger.Warn($"Fluids: the people pass failed in '{_space.World.Name}': {ex}");
    }

    private static float3 Min(float3 a, float3 b) => new(MathF.Min(a.x, b.x), MathF.Min(a.y, b.y), MathF.Min(a.z, b.z));

    private static float3 Max(float3 a, float3 b) => new(MathF.Max(a.x, b.x), MathF.Max(a.y, b.y), MathF.Max(a.z, b.z));
}
