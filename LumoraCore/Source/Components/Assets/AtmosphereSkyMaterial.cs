// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Components;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A sky worked out from sunlight scattering through air: blue overhead, pale at the horizon, warm and dim as the
// sun goes down, with a halo round the sun and the ground below the horizon. The sun is a Light: its facing is
// where the sun sits and its colour times its intensity is the sunlight, so the sky follows the world's sun as it
// moves. With no light the sun stands up and to the side, white.
//
// On a Skybox it is the world's sky; on a mesh, each point shows the sky in the direction the viewer looks
// through it. SkyTint shifts which colours the air scatters (mid grey is the natural sky), AtmosphereThickness
// how much air there is, Exposure the brightness of the whole. -xlinka
[ComponentCategory("Assets/Materials")]
public class AtmosphereSkyMaterial : MaterialProvider, ISkyboxMaterial
{
    [Group("Atmosphere")]
    [Range(0f, 5f, "0.00")]
    public readonly Sync<float> AtmosphereThickness;
    public readonly Sync<color> SkyTint;
    public readonly Sync<color> GroundColor;
    [Range(0f, 8f, "0.00")]
    public readonly Sync<float> Exposure;

    [Group("Sun")]
    public readonly SyncRef<Light> Sun;
    public readonly Sync<AtmosphereSunDisc> SunQuality;
    [Range(0f, 1f, "0.000")]
    public readonly Sync<float> SunSize;

    // Where the sun stands with no light set, the same spot a package's sky falls back to.
    private static readonly float3 UnlitSunDirection = new float3(-0.57735f, 0.57735f, 0.57735f);

    private ChangeFollow? _follow;
    private Slot? _sunSlot;
    private Action<Slot>? _sunMoved;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public AtmosphereSkyMaterial()
    {
        AtmosphereThickness = new Sync<float>(this, 1f);
        SkyTint = new Sync<color>(this, new color(0.5f, 0.5f, 0.5f, 1f));
        GroundColor = new Sync<color>(this, new color(0.25f, 0.25f, 0.25f, 1f));
        Exposure = new Sync<float>(this, 1.3f);
        Sun = new SyncRef<Light>(this);
        SunQuality = new Sync<AtmosphereSunDisc>(this, AtmosphereSunDisc.HighQuality);
        SunSize = new Sync<float>(this, 0.04f);
    }

    // The sunlight as the sky draws it: toward the sun, its colour and its strength. Read by the sky hook too, so
    // the world's sky and an orb wearing this agree.
    public void ResolveSun(out float3 towardSun, out color sunColor, out float intensity)
    {
        var light = Sun.Target;
        if (light == null || light.IsDestroyed || light.Slot == null)
        {
            towardSun = UnlitSunDirection;
            sunColor = color.White;
            intensity = 1f;
            return;
        }
        // A light shines down its slot's -Z, so the sun stands along +Z.
        towardSun = light.Slot.Forward;
        sunColor = light.LightColor.Value;
        intensity = light.Intensity.Value;
    }

    public override void OnChanges()
    {
        base.OnChanges();
        // The light's colour and strength are its own fields; where it faces is its slot's transform, which no
        // field change reports, so that is followed by the slot's move notice. Nothing runs per frame.
        var light = Sun.Target;
        var follow = _follow ??= new ChangeFollow(this);
        follow.Begin();
        follow.AddRef(Sun);
        if (light != null)
        {
            follow.Add(light.LightColor);
            follow.Add(light.Intensity);
        }
        follow.End();
        FollowSunSlot(light?.Slot);
    }

    public override void OnDestroy()
    {
        _follow?.Clear();
        FollowSunSlot(null);
        base.OnDestroy();
    }

    private void FollowSunSlot(Slot? slot)
    {
        if (ReferenceEquals(slot, _sunSlot))
            return;
        if (_sunSlot != null && _sunMoved != null)
            _sunSlot.WorldTransformChanged -= _sunMoved;
        _sunSlot = slot;
        if (slot != null)
            slot.WorldTransformChanged += _sunMoved ??= _ => MarkChangeDirty();
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetCustomShader("res://Shaders/Mat_AtmosphereSky.gdshader");
        ResolveSun(out var towardSun, out var sunColor, out var intensity);

        asset.SetFloat("AtmosphereThickness", AtmosphereThickness.Value);
        asset.SetColor("SkyTint", (colorHDR)SkyTint.Value);
        asset.SetColor("GroundColor", (colorHDR)GroundColor.Value);
        asset.SetFloat("Exposure", Exposure.Value);
        asset.SetFloat3("SunDirection", towardSun);
        asset.SetColor("SunColor", (colorHDR)sunColor);
        asset.SetFloat("SunIntensity", intensity);
        asset.SetFloat("SunSize", SunSize.Value);
        asset.SetInt("SunDisc", (int)SunQuality.Value);
    }
}
