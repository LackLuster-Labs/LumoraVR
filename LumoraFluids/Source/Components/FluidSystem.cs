// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Helio.UI;
using Lumora.Core;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// One body of fluid: its substances, its ledger and one keeper. Particles live only on GPU machines and are never
// sent; what every machine agrees on is the ledger, a sparse grid of settled cells the host writes. The saved
// ledger members are guarded by FluidLedgerRule rather than marked host-only, so a saved cup a guest spawns still
// arrives full; the session-only members are host-only and reset on load. -xlinka
[ComponentCategory("Physics/Fluids")]
public sealed class FluidSystem : Component, ICustomInspectorUI, IInspectorActionHandler
{
    // 64 commands, an entry pair each.
    public const int CommandRingLength = 128;
    public const float DefaultSpacing = 0.016f;

    [Group("Space")]
    // m; 0 = the first substance's
    public readonly Sync<float> Spacing = new(0f);
    // m; 0 = the thinnest spawn volume's depth over 3. Every tier keeps at least this fine a spacing.
    public readonly Sync<float> MaxSpacing = new(0f);
    // m, half sizes are half of this; zero = unbounded
    public readonly Sync<float3> Bounds = new();
    public readonly Sync<float3> BoundsCenter = new();
    // m, local y
    public readonly Sync<float> KillBelow = new(-100f);
    // m; 0 = always drawn
    public readonly Sync<float> MaxViewDistance = new(30f);

    [Group("Substances")]
    public readonly SyncRefList<FluidSubstance> Substances = new();

    [Group("Behaviour")]
    public readonly Sync<bool> Simulate = new(true);
    public readonly Sync<bool> CollideWithWorld = new(true);
    public readonly SyncRefList<Collider> Colliders = new();
    public readonly Sync<bool> StickToPeople = new(true);
    public readonly Sync<bool> PushBodies = new(true);
    public readonly Sync<bool> MixColours = new(false);
    public readonly Sync<bool> KeepStains = new(true);
    public readonly Sync<int> ParticleHint = new(0);
    // m; 0 = 3 x the ledger spacing
    public readonly Sync<float> CellSize = new(0f);

    // The saved ledger (int4 cells, see FluidCell), written by the host.
    [HideInInspector]
    public readonly SyncArray<int4> Cells = new();
    [HideInInspector]
    public readonly SyncArray<int> CellTint = new();
    // x, y = packed RefID of a slot cells are keyed to; at most 255
    [HideInInspector]
    public readonly SyncArray<int4> AnchorSlots = new();
    // at most 1024
    [HideInInspector]
    public readonly SyncArray<int4> Stains = new();
    [HideInInspector]
    public readonly SyncArray<int> StainTint = new();
    [HideInInspector]
    public readonly Sync<int> Epoch = new(0);
    [HideInInspector]
    public readonly Sync<int> LedgerVersion = new(1);

    // Host only. Index 0 is the world; every origin resets on load, so it is never saved either: a guest's saved
    // cup whose load lands after the creation would otherwise carry it into a host-only member.
    [NonPersistent]
    [HideInInspector]
    public readonly SyncArray<int4> Origins = new();
    [NonPersistent]
    public readonly SyncRef<User> Keeper = new();
    // session seconds
    [NonPersistent]
    public readonly Sync<double> KeeperSince = new(0.0);
    // 64 commands x 2
    [NonPersistent]
    [HideInInspector]
    public readonly SyncArray<int4> Commands = new();
    [NonPersistent]
    public readonly Sync<int> CommandSequence = new(0);
    [NonPersistent]
    public readonly Sync<float> Litres = new(0f);

    public bool IsKeeperHere => Keeper.Target is { IsDestroyed: false } keeper && World != null && ReferenceEquals(keeper, World.LocalUser);

    public override void OnAwake()
    {
        base.OnAwake();
        Origins.MarkHostOnly();
        Keeper.MarkHostOnly();
        KeeperSince.MarkHostOnly();
        Commands.MarkHostOnly();
        CommandSequence.MarkHostOnly();
        Litres.MarkHostOnly();
    }

    public override void OnStart()
    {
        base.OnStart();
        FluidSpace.Ensure(World)?.Add(this);
    }

    // Spacing, bounds and the substance list feed the packed records, and the substance records depend on the spacing.
    // The ledger, keeper and ring members change all the time on the host and feed neither.
    protected override void OnSyncMemberChanged(IChangeable member)
    {
        base.OnSyncMemberChanged(member);
        if (IsLedgerOrSession(member) || FluidSpace.Find(World) is not { } space)
            return;
        space.SystemsDirty = true;
        space.SubstancesDirty = true;
    }

    private bool IsLedgerOrSession(IChangeable member)
        => ReferenceEquals(member, Cells) || ReferenceEquals(member, CellTint) || ReferenceEquals(member, AnchorSlots)
        || ReferenceEquals(member, Stains) || ReferenceEquals(member, StainTint) || ReferenceEquals(member, Epoch)
        || ReferenceEquals(member, LedgerVersion) || ReferenceEquals(member, Origins) || ReferenceEquals(member, Keeper)
        || ReferenceEquals(member, KeeperSince) || ReferenceEquals(member, Commands) || ReferenceEquals(member, CommandSequence)
        || ReferenceEquals(member, Litres);

    public override void OnDestroy()
    {
        FluidSpace.Find(World)?.Remove(this);
        base.OnDestroy();
    }

    // SPACING. The substance gives the spacing at the High tier; each tier scales it, never past the system's
    // MaxSpacing; the ledger's cells are sized for the coarsest keeper, so every machine's bins resolve them.

    public float HighSpacing
    {
        get
        {
            float spacing = Spacing.Value;
            if (spacing <= 0f)
                spacing = FirstSubstance?.Spacing.Value ?? DefaultSpacing;
            return spacing > 0f && float.IsFinite(spacing) ? spacing : DefaultSpacing;
        }
    }

    // MaxSpacing, or when it is 0 the thinnest spawn volume's depth over 3, so a puddle is three layers deep on
    // every tier.
    public float EffectiveMaxSpacing
    {
        get
        {
            float max = MaxSpacing.Value;
            if (max > 0f)
                return max;
            float thinnest = 0f;
            if (FluidSpace.Find(World) is { } space)
            {
                foreach (var volume in space.SpawnVolumes)
                {
                    if (volume.IsDestroyed || volume.System.Target != this)
                        continue;
                    float depth = MathF.Abs(volume.Size.Value.y) / 3f;
                    if (depth > 0f && (thinnest <= 0f || depth < thinnest))
                        thinnest = depth;
                }
            }
            return thinnest;
        }
    }

    public float SpacingOn(FluidGpuTier tier) => FluidQuality.Spacing(HighSpacing, EffectiveMaxSpacing, tier);

    public float EffectiveCellSize => FluidLedger.CellSizeOf(this);

    public FluidSubstance? FirstSubstance
    {
        get
        {
            foreach (var substance in Substances)
            {
                if (substance is { IsDestroyed: false })
                    return substance;
            }
            return null;
        }
    }

    public int SubstanceCount
    {
        get
        {
            int count = 0;
            foreach (var substance in Substances)
            {
                if (substance is { IsDestroyed: false })
                    count++;
            }
            return count;
        }
    }

    // The packed index of a substance among this system's live ones; 0 (the first) when it is not one of them.
    public int SubstanceIndexOf(FluidSubstance? wanted)
    {
        if (wanted == null)
            return 0;
        int index = 0;
        foreach (var substance in Substances)
        {
            if (substance is not { IsDestroyed: false })
                continue;
            if (ReferenceEquals(substance, wanted))
                return index;
            index++;
        }
        return 0;
    }

    public colorHDR TintOf(int substanceIndex)
    {
        int index = 0;
        foreach (var substance in Substances)
        {
            if (substance is not { IsDestroyed: false })
                continue;
            if (index++ == substanceIndex)
                return substance.Tint.Value;
        }
        return FirstSubstance?.Tint.Value ?? colorHDR.White;
    }

    // Litres the ledger holds within radius of a world point. Every machine reads the same cells, the headless host
    // included; anchored cells ride on props and people and are not counted where they lie.
    public float LitresIn(float3 center, float radius)
    {
        if (Slot == null || radius <= 0f || !float.IsFinite(radius))
            return 0f;
        float size = EffectiveCellSize;
        var local = Slot.GlobalPointToLocal(center);
        float scale = Slot.GlobalScale.x;
        float r = scale > 1e-6f ? radius / scale : radius;
        double total = 0;
        foreach (var cell in Cells)
        {
            if (FluidCell.Fill(cell) == 0 || FluidCell.Anchor(cell) != FluidCell.NoAnchor)
                continue;
            if (float3.DistanceSquared(FluidCell.Centre(cell, size), local) <= r * r)
                total += FluidCell.Litres(cell, size);
        }
        return (float)total;
    }

    public double LedgerLitres()
    {
        float size = EffectiveCellSize;
        double total = 0;
        foreach (var cell in Cells)
            total += FluidCell.Litres(cell, size);
        return total;
    }

    // Lets everything this machine simulates come to rest. The host puts it on the command ring, so every GPU machine
    // settles it once and the keeper reports a full cell write.
    public void Settle() => FluidSpace.Find(World)?.Command(this, FluidCommandKind.Settle);

    // Empties the system everywhere. Host-side only: a guest's machine has no right to the ring and asks through its
    // own request ring instead.
    public void Clear() => FluidSpace.Find(World)?.Command(this, FluidCommandKind.ClearAll);

    public void BuildInspectorBody(UIBuilder ui)
    {
        var space = FluidSpace.Find(World);
        int index = space?.SystemIndex(this) ?? -1;
        InspectorStats.AddRow(ui, "Litres", $"{LedgerLitres():0.###} L in {Cells.Count} cells");
        if (space != null && index >= 0 && space.Simulating)
            InspectorStats.AddRow(ui, "Particles here", $"{space.StatusOf(index).Live} ({space.StatusOf(index).Awake} awake)");
        InspectorStats.AddRow(ui, "Keeper", KeeperText(space));
        InspectorStats.AddRow(ui, "This machine", PathText(space));
        FluidInspectorStats.ForSystem(ui, this);
        foreach (var warning in Warnings(space))
            InspectorStats.AddRow(ui, "Warning", warning);

        InspectorUI.FixedRow(ui.Root, "Actions", InspectorStats.RowHeight, out var row, ui.Root);
        InspectorUI.RelayButton(row, this, "quality", "This machine: " + FluidQuality.Describe(FluidQuality.Requested), 0f);
        if (World?.IsAuthority == true)
        {
            InspectorUI.RelayButton(row, this, "settle", "Settle", 90f);
            InspectorUI.RelayButton(row, this, "clear", "Clear", 90f);
        }
    }

    public void HandleInspectorAction(string argument)
    {
        switch (argument)
        {
            case "settle":
                Settle();
                break;
            case "clear":
                Clear();
                break;
            case "quality":
                // Auto, then View only, then each tier up to Ultra, and round again.
                int next = FluidQuality.Requested + 1;
                FluidQuality.SetRequested(next > (int)FluidGpuTier.Ultra ? FluidQuality.Auto : next);
                break;
        }
    }

    private string KeeperText(FluidSpace? space)
    {
        var keeper = Keeper.Target;
        if (keeper == null || keeper.IsDestroyed)
            return space?.ViewOnly == true || FluidGpu.Current == null ? "nobody: no GPU here" : "nobody yet";
        if (ReferenceEquals(keeper, World?.LocalUser))
            return "you";
        if (keeper.IsHost)
            return "host";
        return keeper.UserName.Value ?? "someone";
    }

    private static string PathText(FluidSpace? space)
    {
        if (space == null || !space.PathDecided)
            return "not started";
        if (space.Simulating)
            return $"GPU, {space.Tier}";
        return FluidGpu.Current is { } gpu ? gpu.Status : "view only";
    }

    private static System.Collections.Generic.IEnumerable<string> Warnings(FluidSpace? space)
    {
        if (space == null)
            yield break;
        if (space.Simulating && FluidGpu.Current?.DrawingInRealTime == false)
            yield return "this machine is not drawing in real time";
        if (space.Colliders.Holding)
            yield return "a mesh collider's grid is building";
        if (space.Colliders.LeftOutMesh)
            yield return "a mesh collider was left out while its grid built";
        if (space.Colliders.Splits > 0)
            yield return "the collider query was split";
        var summary = space.LastSummary;
        if (summary.NeighbourOverflow > 0)
            yield return "neighbour lists overflowed";
        if (summary.SlabOverflow > 0)
            yield return "surface slabs overflowed";
        if (summary.PoolEmpty > 0)
            yield return "the strand particle pool ran out";
    }
}
