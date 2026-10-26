// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// Emits light in the scene.
[ComponentCategory("Rendering")]
public class Light : ImplementableComponent, IPrimaryColorSource
{
    // Point, Directional or Spot
    public readonly Sync<LightType> Type = new();

    public readonly Sync<color> LightColor = new();

    // brightness multiplier
    public readonly Sync<float> Intensity = new();

    // Point and Spot only
    public readonly Sync<float> Range = new();

    // degrees, Spot only
    public readonly Sync<float> SpotAngle = new();

    public readonly Sync<ShadowType> Shadows = new();

    // 0-1
    public readonly Sync<float> ShadowStrength = new();

    // keeps surfaces from shadowing themselves
    public readonly Sync<float> ShadowBias = new();

    public readonly Sync<float> ShadowNormalBias = new();

    public readonly Sync<float> ShadowNearPlane = new();

    // Metres from the camera that a DIRECTIONAL light bothers to shadow, and the single biggest knob on
    // what a sun costs. The renderer draws every caster inside this radius into the cascades once per
    // cascade, so leaving it at the platform default of a hundred metres means a showcase world hands the
    // whole map to the sun four times a frame to get shadows you cannot see past the third area. Sixty is
    // a room and its surroundings; drop it further for an indoor world, raise it for a landscape where the
    // far hills are supposed to shade each other. Ignored by point and spot lights, which are bounded by
    // their own Range. -xlinka
    public readonly Sync<float> ShadowMaxDistance = new();

    // How many cascades a directional light splits ShadowMaxDistance into. Four is the best-looking and
    // the most expensive; Two halves the passes and is usually indistinguishable once the max distance is
    // sane, because the near cascade no longer has to cover the whole world. Orthogonal is one pass, sharp
    // near and mushy far. -xlinka
    public readonly Sync<ShadowSplitMode> ShadowSplits = new();

    // Fade a point or spot light out as the viewer walks away from it, in metres: nothing happens until
    // DistanceFadeBegin, then it dissolves over DistanceFadeLength and stops being submitted at all. 0
    // length turns the whole thing off, which is the default. This is what makes a room full of little
    // lamps affordable - each one is only doing work while somebody is near enough to see it. Directional
    // lights ignore it: they have no position to be far from. -xlinka
    public readonly Sync<float> DistanceFadeBegin = new();
    public readonly Sync<float> DistanceFadeLength = new();

    // Drop this light's SHADOW before dropping the light itself, in metres from the viewer.
    //
    // The cheapest lever there is for a room full of lamps: an unshadowed light is close to free while
    // its shadow is six cube-face renders, so a lamp across the room can keep lighting the scene and
    // stop casting long before it stops being submitted at all. 0 keeps the shadow for as long as the
    // light lives, which is the old behaviour. -xlinka
    public readonly Sync<float> ShadowFadeDistance = new();

    // Masks and tints this light, projector-style: the texture is multiplied into the light before it
    // hits anything, so a window frame, a gobo or a caustic pattern costs one texture instead of
    // geometry. Alpha masks, RGB tints. Null = no cookie.
    //
    // POINT AND SPOT ONLY. There is no directional-light projector in this renderer at all, so a cookie
    // on a directional light is dropped and says so once in the log rather than quietly doing nothing.
    // The old CookieSize field existed purely to size a directional cookie and has been removed for the
    // same reason. -xlinka
    public readonly AssetRef<TextureAsset> Cookie = new();

    // How a point or spot light fades over its range. Window keeps nearly full strength across the range
    // and fades at the edge, which is what content made here is lit with. Inverse falls off like
    // 1 / (1 + 25 (d / range)^2), the curve lights from imported packages were authored against; with
    // Window those rooms flood to white. Directional lights ignore it. -xlinka
    public readonly Sync<LightFalloff> Falloff = new();

    // Which way the light points, turned from its slot. Identity shines along the slot's -Z. A light that came
    // from content aimed along +Z carries a half turn here instead of having its slot, and everything on it,
    // turned round. -xlinka
    public readonly Sync<floatQ> RotationOffset = new();

    public override void OnInit()
    {
        base.OnInit();

        // LightType.Point is value 0 - C# default, but set for clarity
        // Type.Value = LightType.Point; // skip, it's enum 0
        LightColor.Value        = new color(1f, 1f, 1f, 1f);
        Intensity.Value         = 1f;
        Range.Value             = 10f;
        SpotAngle.Value         = 60f;   // FULL cone; the hook halves it for Godot
        Shadows.Value           = ShadowType.Hard;
        ShadowStrength.Value    = 1f;
        ShadowFadeDistance.Value = 0f;
        ShadowBias.Value        = DefaultShadowBias;
        ShadowNormalBias.Value  = DefaultShadowNormalBias;
        ShadowNearPlane.Value   = 0.2f;
        ShadowMaxDistance.Value = 40f;
        ShadowSplits.Value      = ShadowSplitMode.Two;
        DistanceFadeBegin.Value = 0f;
        DistanceFadeLength.Value = 0f;
        RotationOffset.Value    = floatQ.Identity;
        // Cookie = default (C# default null, skip)
    }

    // Both biases go straight to the renderer in its own units, the normal bias in shadow-map texels along
    // the surface normal. The old pair, 0.05 and 0.4, striped any face turned close to edge-on to the light:
    // a tipped box's top under the Grid sun, a flat slab under a spot. Two texels clears both without
    // lifting a box's shadow off the floor. -xlinka
    public const float DefaultShadowBias = 0.1f;
    public const float DefaultShadowNormalBias = 2f;
    private const float OldShadowBias = 0.05f;
    private const float OldShadowNormalBias = 0.4f;

    public override void OnStart()
    {
        base.OnStart();
        // Every light saved before the defaults moved still carries the old pair. Only the untouched pair
        // moves; a light anyone tuned keeps its numbers. -xlinka
        if (World?.IsAuthority == true
            && MathF.Abs(ShadowBias.Value - OldShadowBias) < 1e-6f
            && MathF.Abs(ShadowNormalBias.Value - OldShadowNormalBias) < 1e-6f)
        {
            ShadowBias.Value = DefaultShadowBias;
            ShadowNormalBias.Value = DefaultShadowNormalBias;
        }
    }

    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
    }

    // The picked colour is the LightColor as authored, NOT multiplied by Intensity: intensity is a
    // brightness knob on the same hue, and folding it in means eyedropping a dim lamp hands you black.
    // -xlinka
    public bool TryGetPrimaryColor(out colorHDR color)
    {
        color = LightColor.Value;
        return true;
    }
}

public enum LightType
{
    Point,        // Omni-directional point light
    Directional,  // Directional light (sun)
    Spot          // Spot light with cone
}

public enum ShadowType
{
    None,    // No shadows
    Hard,    // Hard shadows (no filtering)
    Soft     // Soft shadows (PCF filtering)
}

// Cascade count for a directional light's shadow, cheapest first.
public enum LightFalloff
{
    Window,
    Inverse,
}

public enum ShadowSplitMode
{
    Orthogonal,  // one cascade
    Two,         // two cascades
    Four         // four cascades
}
