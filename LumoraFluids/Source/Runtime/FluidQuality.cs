// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;

namespace Lumora.Fluids;

// The tier table and this machine's choice of tier. A tier changes local spacing, detail and looks, never the ledger,
// anyone else's numbers or gameplay, so two machines on different tiers still agree on every puddle.
//
// The awake caps are what bounds the frame: at hero sizes the cost is the dispatch floor, past a few thousand awake
// particles it is the particles. They come from G0m run on the real kernels (a 3080 Ti at full clock, bound by goo):
// Ultra 20k, High 5k, Medium 3k, and Low 1k at 2 substeps, which in practice is the Lite rung. They hold until the
// solver's neighbour-list pass (A11), after which the sweep runs again and raises them. Calibration still picks the
// tier per machine.
//
// The Standalone row stays for a desktop user who asks for it by hand. Android builds and the standalone Frame never
// simulate in v1 (the device check makes them view only), so no headset ever runs it. -xlinka
public static class FluidQuality
{
    public const int Auto = -1;
    public const int ViewOnly = 0;

    // Indexed by (int)FluidGpuTier: None, Standalone, Low, Medium, High, Ultra.
    private static readonly float[] SpacingScales = { 1.6f, 1.6f, 1.6f, 1.25f, 1f, 0.6f };
    private static readonly int[] SubstepCaps = { 2, 2, 2, 4, 4, 8 };
    private static readonly int[] AwakeCaps = { 0, 1000, 1000, 3000, 5000, 20000 };
    private static readonly int[] TotalCaps = { 0, 6000, 16000, 32000, 64000, 128000 };
    private static readonly int[] DispatchBudgets = { 0, 25, 32, 64, 64, 64 };
    private static readonly int[] BondSlotCounts = { 6, 6, 6, 8, 12, 12 };
    private static readonly int[] RefinePools = { 0, 128, 128, 512, 2048, 4096 };
    private static readonly int[] StrandsDrawnCaps = { 0, 8, 12, 24, 48, 64 };
    private static readonly int[] StrandSideCounts = { 4, 4, 4, 6, 8, 12 };
    private static readonly int[] RemoteAnchorPools = { 0, 64, 128, 256, 512, 1024 };
    private static readonly int[] SpotsDrawnCaps = { 12, 12, 12, 24, 48, 48 };
    private static readonly int[] TriangleCapacities = { 0, 16384, 16384, 32768, 98304, 196608 };
    private static readonly float[] SurfaceCellScales = { 1f, 1f, 1f, 1f, 0.75f, 0.5f };
    // ms of GPU for the simulation alone (12.1), the figure calibration fits a tier into
    private static readonly double[] SimulationBudgets = { 0, 0.5, 0.6, 0.9, 1.2, 2.2 };
    // ms of GPU between the chain's two timestamps: simulation plus surface, strands, spray and residue (12.1)
    private static readonly double[] ChainBudgets = { 0, 0.75, 0.8, 1.2, 1.6, 2.9 };
    // ms of main thread, recording included
    private static readonly double[] MainBudgets = { 0, 0.4, 0.5, 0.5, 0.5, 0.6 };

    public static FluidGpuTier DefaultTier => InitialTier(integrated: false);

    public static FluidGpuTier Clamp(FluidGpuTier tier) => tier < FluidGpuTier.None ? FluidGpuTier.None : tier > FluidGpuTier.Ultra ? FluidGpuTier.Ultra : tier;

    public static float SpacingScale(FluidGpuTier tier) => SpacingScales[(int)Clamp(tier)];

    public static int MaxSubsteps(FluidGpuTier tier) => SubstepCaps[(int)Clamp(tier)];

    public static int AwakeCap(FluidGpuTier tier) => AwakeCaps[(int)Clamp(tier)];

    public static int TotalCap(FluidGpuTier tier) => TotalCaps[(int)Clamp(tier)];

    public static int DispatchBudget(FluidGpuTier tier) => DispatchBudgets[(int)Clamp(tier)];

    public static int BondSlots(FluidGpuTier tier) => BondSlotCounts[(int)Clamp(tier)];

    public static int RefinePool(FluidGpuTier tier) => RefinePools[(int)Clamp(tier)];

    public static int StrandsDrawn(FluidGpuTier tier) => StrandsDrawnCaps[(int)Clamp(tier)];

    public static int StrandSides(FluidGpuTier tier) => StrandSideCounts[(int)Clamp(tier)];

    public static int RemoteAnchorPool(FluidGpuTier tier) => RemoteAnchorPools[(int)Clamp(tier)];

    public static int SpotsDrawn(FluidGpuTier tier) => SpotsDrawnCaps[(int)Clamp(tier)];

    public static int TriangleCapacity(FluidGpuTier tier) => TriangleCapacities[(int)Clamp(tier)];

    public static float SurfaceCellScale(FluidGpuTier tier) => SurfaceCellScales[(int)Clamp(tier)];

    public static double SimulationBudgetMs(FluidGpuTier tier) => SimulationBudgets[(int)Clamp(tier)];

    public static double ChainBudgetMs(FluidGpuTier tier) => ChainBudgets[(int)Clamp(tier)];

    public static double MainBudgetMs(FluidGpuTier tier) => MainBudgets[(int)Clamp(tier)];

    // Spray is drawn from High up; Medium and below spend the time on the bulk instead.
    public static bool Spray(FluidGpuTier tier) => tier >= FluidGpuTier.High;

    // A drawn radius floor in metres, cosmetic only: snaps always use the substance's MinThread.
    public static float DrawRadiusFloor(FluidGpuTier tier) => tier switch
    {
        FluidGpuTier.Standalone or FluidGpuTier.Low => 0.001f,
        FluidGpuTier.Medium => 0.0005f,
        _ => 0f,
    };

    // The step below on the ladder's "tier -1": the next coarser spacing with its caps. Low has nowhere to go; its
    // last steps are Lite and View instead.
    public static FluidGpuTier Below(FluidGpuTier tier) => tier > FluidGpuTier.Low ? tier - 1 : tier;

    // The spacing this machine runs a system at: the substance's High-tier spacing times the tier's scale, never
    // coarser than the system's MaxSpacing, so the hero puddle stays three layers deep on every tier.
    public static float Spacing(float highSpacing, float maxSpacing, FluidGpuTier tier)
    {
        float d = highSpacing * SpacingScale(tier);
        if (maxSpacing > 0f && d > maxSpacing)
            d = maxSpacing;
        return d > 0f && float.IsFinite(d) ? d : highSpacing;
    }

    // Before this machine has a calibration: the Deck and integrated GPUs start on Low, a discrete desktop card on
    // Medium, a headset or phone on Standalone. The ladder and the calibration move it from there.
    public static FluidGpuTier InitialTier(bool integrated)
    {
        if (Engine.IsStandaloneHeadset || Engine.IsMobilePlatform)
            return FluidGpuTier.Standalone;
        if (SteamDeckPlatform.IsSteamDeck || integrated)
            return FluidGpuTier.Low;
        return FluidGpuTier.Medium;
    }

    // The key that applies right now: a headset session keeps its own choice, so a desktop that runs Ultra flat can
    // still drop a step in VR where the frame is twice the pixels.
    public static string ActiveKey => InHeadset ? FluidSettingKeys.QualityVR : FluidSettingKeys.Quality;

    private static bool InHeadset => Engine.Current?.InputInterface?.IsVRActive == true;

    // -1 Auto, 0 view only, 1-5 Standalone to Ultra; anything else reads as Auto.
    public static int Requested
    {
        get
        {
            int value = Settings.ReadValue(ActiveKey, Auto);
            return value is >= Auto and <= (int)FluidGpuTier.Ultra ? value : Auto;
        }
    }

    // What this machine runs: the user's pick when there is one, else what Auto found.
    public static FluidGpuTier Resolve(FluidGpuTier auto)
    {
        int requested = Requested;
        if (requested == Auto)
            return Clamp(auto);
        return requested == ViewOnly ? FluidGpuTier.None : (FluidGpuTier)requested;
    }

    // True for the two keys that pick a tier; the device re-resolves when either changes.
    public static bool IsQualityKey(string key)
        => string.Equals(key, FluidSettingKeys.Quality, StringComparison.Ordinal)
        || string.Equals(key, FluidSettingKeys.QualityVR, StringComparison.Ordinal);

    public static void SetRequested(int value)
        => Settings.WriteValue(ActiveKey, Math.Clamp(value, Auto, (int)FluidGpuTier.Ultra));

    public static string Describe(int requested) => requested switch
    {
        Auto => "Auto",
        ViewOnly => "View only",
        _ => ((FluidGpuTier)Math.Clamp(requested, 1, 5)).ToString(),
    };
}
