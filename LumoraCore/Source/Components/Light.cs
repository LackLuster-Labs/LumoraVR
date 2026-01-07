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
    public Sync<LightType> Type { get; private set; }

    /// <summary>
    /// Light color (RGB)
    /// </summary>
    public Sync<color> LightColor { get; private set; }

    /// <summary>
    /// Light intensity (brightness multiplier)
    /// </summary>
    public Sync<float> Intensity { get; private set; }

    /// <summary>
    /// Range of the light (for Point and Spot lights)
    /// </summary>
    public Sync<float> Range { get; private set; }

    /// <summary>
    /// Spot angle in degrees (for Spot lights)
    /// </summary>
    public Sync<float> SpotAngle { get; private set; }

    /// <summary>
    /// Shadow casting mode
    /// </summary>
    public Sync<ShadowType> Shadows { get; private set; }

    /// <summary>
    /// Shadow strength (0-1)
    /// </summary>
    public Sync<float> ShadowStrength { get; private set; }

    /// <summary>
    /// Shadow bias to prevent acne
    /// </summary>
    public Sync<float> ShadowBias { get; private set; }

    /// <summary>
    /// Shadow normal bias
    /// </summary>
    public Sync<float> ShadowNormalBias { get; private set; }

    /// <summary>
    /// Shadow near plane distance
    /// </summary>
    public Sync<float> ShadowNearPlane { get; private set; }

    /// <summary>
    /// Cookie texture for light masking (TODO: Replace with platform-agnostic texture type)
    /// </summary>
    public Sync<object> Cookie { get; private set; }

    /// <summary>
    /// Cookie size for directional lights
    /// </summary>
    public Sync<float> CookieSize { get; private set; }

    public override void OnAwake()
    {
        base.OnAwake();

        // Initialize sync members
        Type = new Sync<LightType>(this, LightType.Point);
        LightColor = new Sync<color>(this, new color(1f, 1f, 1f, 1f));
        Intensity = new Sync<float>(this, 1f);
        Range = new Sync<float>(this, 10f);
        SpotAngle = new Sync<float>(this, 30f);
        Shadows = new Sync<ShadowType>(this, ShadowType.Hard);
        ShadowStrength = new Sync<float>(this, 1f);
        ShadowBias = new Sync<float>(this, 0.05f);
        ShadowNormalBias = new Sync<float>(this, 0.4f);
        ShadowNearPlane = new Sync<float>(this, 0.2f);
        Cookie = new Sync<object>(this, default);
        CookieSize = new Sync<float>(this, 10f);
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
