// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// Engine-owned CPU particle system. Platform hooks simulate and render instances.
/// </summary>
[ComponentCategory("Rendering")]
public sealed class ParticleSystem : ImplementableComponent
{
    public readonly Sync<int> MaxParticles = new();
    public readonly Sync<float> EmissionRate = new();
    public readonly Sync<int> BurstCount = new();
    public readonly Sync<float> BurstInterval = new();
    public readonly Sync<float3> EmitterExtents = new();
    public readonly Sync<float> SpawnHeight = new();
    public readonly Sync<float> Lifetime = new();
    public readonly Sync<float> LifetimeVariance = new();
    public readonly Sync<float> StartSize = new();
    public readonly Sync<float> EndSize = new();
    public readonly Sync<float> InitialSpeed = new();
    public readonly Sync<float> SpeedVariance = new();
    public readonly Sync<float> Spread = new();
    public readonly Sync<float> Gravity = new();
    public readonly Sync<colorHDR> StartColor = new();
    public readonly Sync<colorHDR> EndColor = new();
    public readonly Sync<float> EmissionStrength = new();
    public readonly Sync<int> Seed = new();
    public readonly Sync<int> RenderQueue = new();

    public override void OnInit()
    {
        base.OnInit();

        MaxParticles.Value = 360;
        EmissionRate.Value = 18f;
        BurstCount.Value = 5;
        BurstInterval.Value = 0.42f;
        EmitterExtents.Value = new float3(24.5f, 0f, 24.5f);
        SpawnHeight.Value = 0.035f;
        Lifetime.Value = 0.72f;
        LifetimeVariance.Value = 0.18f;
        StartSize.Value = 0.065f;
        EndSize.Value = 0.018f;
        InitialSpeed.Value = 1.25f;
        SpeedVariance.Value = 0.45f;
        Spread.Value = 0.34f;
        Gravity.Value = -1.15f;
        StartColor.Value = new colorHDR(0.90f, 1.00f, 0.94f, 0.95f);
        EndColor.Value = new colorHDR(1.00f, 0.56f, 0.88f, 0.0f);
        EmissionStrength.Value = 1.4f;
        Seed.Value = 1771;
        RenderQueue.Value = 60;
    }
}
