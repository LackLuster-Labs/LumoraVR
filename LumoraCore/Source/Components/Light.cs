// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

/// <summary>
/// Emits light in the scene.
/// </summary>
[ComponentCategory("Rendering")]
public class Light : ImplementableComponent
{
    /// <summary>
    /// Type of light (Point, Directional, Spot)
    /// </summary>
    public readonly Sync<LightType> Type = new();

    /// <summary>
    /// Light color (RGB)
    /// </summary>
    public readonly Sync<color> LightColor = new();

    /// <summary>
    /// Light intensity (brightness multiplier)
    /// </summary>
    public readonly Sync<float> Intensity = new();

    /// <summary>
    /// Range of the light (for Point and Spot lights)
    /// </summary>
    public readonly Sync<float> Range = new();

    /// <summary>
    /// Spot angle in degrees (for Spot lights)
    /// </summary>
    public readonly Sync<float> SpotAngle = new();

    /// <summary>
    /// Shadow casting mode
    /// </summary>
    public readonly Sync<ShadowType> Shadows = new();

    /// <summary>
    /// Shadow strength (0-1)
    /// </summary>
    public readonly Sync<float> ShadowStrength = new();

    /// <summary>
    /// Shadow bias to prevent acne
    /// </summary>
    public readonly Sync<float> ShadowBias = new();

    /// <summary>
    /// Shadow normal bias
    /// </summary>
    public readonly Sync<float> ShadowNormalBias = new();

    /// <summary>
    /// Shadow near plane distance
    /// </summary>
    public readonly Sync<float> ShadowNearPlane = new();

    /// <summary>
    /// Cookie texture for light masking (TODO: Replace with platform-agnostic texture type)
    /// </summary>
    public readonly Sync<object> Cookie = new();

    /// <summary>
    /// Cookie size for directional lights
    /// </summary>
    public readonly Sync<float> CookieSize = new();

    public override void OnInit()
    {
        base.OnInit();

        // LightType.Point is value 0 — C# default, but set for clarity
        // Type.Value = LightType.Point; // skip, it's enum 0
        LightColor.Value = new color(1f, 1f, 1f, 1f);
        Intensity.Value = 1f;
        Range.Value = 10f;
        SpotAngle.Value = 30f;
        Shadows.Value = ShadowType.Hard;
        ShadowStrength.Value = 1f;
        ShadowBias.Value = 0.05f;
        ShadowNormalBias.Value = 0.4f;
        ShadowNearPlane.Value = 0.2f;
        // Cookie = default (C# default null, skip)
        CookieSize.Value = 10f;
    }

    public override void OnStart()
    {
        base.OnStart();
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
    }
}

/// <summary>
/// Light types.
/// </summary>
public enum LightType
{
    Point,        // Omni-directional point light
    Directional,  // Directional light (sun)
    Spot          // Spot light with cone
}

/// <summary>
/// Shadow types.
/// </summary>
public enum ShadowType
{
    None,    // No shadows
    Hard,    // Hard shadows (no filtering)
    Soft     // Soft shadows (PCF filtering)
}
