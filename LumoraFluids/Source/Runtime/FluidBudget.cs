// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;

namespace Lumora.Fluids;

// GPU time is a reading, not permission to change the simulation. Only a user-selected tier changes its
// spacing and substeps; a slow frame must not throttle a pour, reseed it or stop it. -xlinka
public sealed class FluidBudget
{
    public FluidBudget(FluidGpuTier machineTier, string owner = "") => MachineTier = FluidQuality.Clamp(machineTier);

    public FluidGpuTier MachineTier { get; private set; }
    public FluidGpuTier Tier => MachineTier;
    public int TierVersion { get; private set; }
    public double LastGpuMs { get; private set; } = double.NaN;
    public int FramesRun { get; private set; }

    public bool View => Tier == FluidGpuTier.None;
    public bool Healthy => !View;
    public bool Lite => false;
    public float SleepScale => 1f;
    public bool WaterSingleIteration => false;
    public bool RemoteRefineOff => false;
    public bool RemoteSkinOff => false;
    public int SubstepCut => 0;
    public bool HalfRateBricks => false;
    public bool PauseUnkept => false;
    public bool ThrottleEmitters => false;
    public int Step => 0;
    public int TierDrops => 0;
    public float Scale => 1f;
    public bool ReseedHeld { get; set; }

    public void SetMachineTier(FluidGpuTier tier)
    {
        tier = FluidQuality.Clamp(tier);
        if (tier == MachineTier)
            return;
        MachineTier = tier;
        TierVersion++;
    }

    public void Report(double gpuMs, double now) => Report(gpuMs, now, true);

    public void Report(double gpuMs, double now, bool framePressed)
    {
        if (!double.IsFinite(gpuMs) || gpuMs < 0)
            return;
        LastGpuMs = gpuMs;
        FramesRun++;
    }

    public string Describe() => View ? "View only (selected)" : $"{Tier}, automatic throttling off";
}
