// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using Lumora.Core;
using Lumora.Core.Input;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The keeper's coat proposals for view-only people. A machine that never simulates cannot see its own user step in
// goo, so the keeper says what its simulation of that person found: bake candidates (region, litres, substance, the
// cell they came from), contact bits, stuck strength per limb. 4 Hz while the person touches fluid, nothing otherwise.
// Only the wearer acts on them: it applies its own stick mode and clamps, writes its own spots, contacts and limbs,
// and sends Pickups for what it baked, exactly as if its own GPU had found it. Each person stays the only writer of
// their own record, and a proposal cannot make goo from nothing: Pickups are clamped to the cells as always.
//
// Payload: x = person allocation byte 8 | body node 8 | substance 4 | proposal kind 4; y = uL (bake), contact bits
// (contact) or stuck strength x 65535 (limb); z = the cell's ledger index (bake) or limb index (limb); w = tint
// RGBA8 (bake). -xlinka
public sealed class FluidCoatProposals
{
    public const int KindBake = 0;
    public const int KindContact = 1;
    public const int KindLimb = 2;

    private readonly List<FluidReportEntry> _pending = new();

    public int Proposed { get; private set; }

    public static int4 Bake(User person, FluidSystem system, BodyNode part, double litres, int substance, int cellIndex, uint tint)
        => new(Header(person, part, substance, KindBake), (int)Math.Clamp(Math.Round(litres * 1e6), 0, int.MaxValue), cellIndex, unchecked((int)tint));

    public static int4 Contact(User person, int bits) => new(Header(person, BodyNode.NONE, 0, KindContact), bits, 0, 0);

    public static int4 Limb(User person, int limb, float stuck)
        => new(Header(person, BodyNode.NONE, 0, KindLimb), (int)Math.Clamp(MathF.Round(stuck * 65535f), 0, 65535), limb, 0);

    private static int Header(User person, BodyNode part, int substance, int kind)
        => person.AllocationID.Value | (((int)part & 255) << 8) | ((substance & 15) << 16) | ((kind & 15) << 20);

    public static byte PersonOf(int4 payload) => (byte)(payload.x & 255);

    public static BodyNode PartOf(int4 payload) => (BodyNode)((payload.x >> 8) & 255);

    public static int SubstanceOf(int4 payload) => (payload.x >> 16) & 15;

    public static int KindOf(int4 payload) => (payload.x >> 20) & 15;

    public static double LitresOf(int4 payload) => payload.y * 1e-6;

    // Keeper: queue a proposal for a person who is view only. Ignored for anyone with their own GPU.
    public void Add(User person, FluidSystem system, int4 payload)
    {
        if (person == null || system == null)
            return;
        var record = FluidProvenance.Of(person);
        if (record == null || FluidKeeperElection.TierOf(record.Gpu.Value) != FluidGpuTier.None)
            return;
        _pending.Add(new FluidReportEntry(system.ReferenceID.RawValue, FluidReportKind.CoatProposal, 0, 0, payload));
        Proposed++;
    }

    // Into the keeper's next report.
    public void Drain(List<FluidReportEntry> output)
    {
        output.AddRange(_pending);
        _pending.Clear();
    }

    // Wearer: the proposals for me in what I read off a ring, from the current keeper of their system only.
    public static void For(User me, User sender, World world, IReadOnlyList<FluidReportEntry> entries, List<(FluidSystem System, int4 Payload)> output)
    {
        if (me == null || sender == null)
            return;
        foreach (var e in entries)
        {
            if (e.Kind != FluidReportKind.CoatProposal || PersonOf(e.Payload) != me.AllocationID.Value)
                continue;
            if (world.ReferenceController.GetObjectOrNull(new RefID(e.System)) is not FluidSystem system)
                continue;
            if (!ReferenceEquals(system.Keeper.Target, sender))
                continue;
            output.Add((system, e.Payload));
        }
    }
}
