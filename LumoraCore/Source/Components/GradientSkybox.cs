// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// World skybox using a gradient sky shader with a procedural sun.
/// </summary>
[ComponentCategory("Rendering")]
public sealed class GradientSkybox : ImplementableComponent
{
    public readonly Sync<color> TopColor;
    public readonly Sync<color> HorizonColor;
    public readonly Sync<color> BottomColor;
    public readonly Sync<color> SunColor;
    public readonly Sync<float3> SunDirection;
    public readonly Sync<float> SunSize;
    public readonly Sync<float> SunIntensity;
    public readonly Sync<float> SunGlowPower;
    public readonly Sync<float> AmbientEnergy;

    public GradientSkybox()
    {
        TopColor = new Sync<color>(this, new color(0.06f, 0.16f, 0.32f, 1f));
        HorizonColor = new Sync<color>(this, new color(0.38f, 0.74f, 0.90f, 1f));
        BottomColor = new Sync<color>(this, new color(0.015f, 0.045f, 0.08f, 1f));
        SunColor = new Sync<color>(this, new color(1.0f, 0.82f, 0.48f, 1f));
        SunDirection = new Sync<float3>(this, new float3(-0.38f, 0.58f, -0.72f));
        SunSize = new Sync<float>(this, 0.032f);
        SunIntensity = new Sync<float>(this, 1.8f);
        SunGlowPower = new Sync<float>(this, 96f);
        AmbientEnergy = new Sync<float>(this, 0.55f);
    }
}
