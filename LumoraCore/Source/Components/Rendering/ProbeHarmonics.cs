// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Helio.UI;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Core.Components;

// What a reflection probe sees, as nine spherical harmonic coefficients for an AmbientLight to take.
//
// A probe showing a stored cubemap is read from that cubemap. Any other probe is read from the world's sky:
// the renderer keeps a scene capture to itself, so the sky is the part of what the probe sees that can be
// known here, and for a probe that captures the sky alone it is all of it. Each band has its own scale, so
// the ambient can be brighter overall while staying flatter across directions.
//
// Worked out by the authority and replicated, so every peer lights the world the same. -xlinka
[ComponentCategory("Rendering")]
public class ProbeHarmonics : Component, ICustomInspectorUI
{
    public readonly SyncRef<ReflectionProbe> Probe;

    // The result. Point a CopyValue at an AmbientLight from here.
    public readonly Sync<SphericalHarmonicsL2> Harmonics;

    [Range(0.1f, 4f, "0.00")]
    public readonly Sync<float> Band0Scale;

    [Range(0.1f, 4f, "0.00")]
    public readonly Sync<float> Band1Scale;

    [Range(0.1f, 4f, "0.00")]
    public readonly Sync<float> Band2Scale;

    private string _source = "nothing read yet";
    private float _recheck;

    public ProbeHarmonics()
    {
        Probe = new SyncRef<ReflectionProbe>(this);
        Harmonics = new Sync<SphericalHarmonicsL2>(this, default);
        Band0Scale = new Sync<float>(this, 1f);
        Band1Scale = new Sync<float>(this, 1f);
        Band2Scale = new Sync<float>(this, 1f);
    }

    public override void OnAttach()
    {
        base.OnAttach();
        Probe.Target ??= Slot.GetComponent<ReflectionProbe>();
    }

    public override void OnChanges()
    {
        base.OnChanges();
        Refresh();
    }

    // The sky's picture and a probe's cubemap load in their own time and say nothing to this, so look again
    // now and then. A read is a few dozen multiplies.
    public override void OnUpdate(float delta)
    {
        base.OnUpdate(delta);
        _recheck -= delta;
        if (_recheck > 0f)
            return;
        _recheck = 1f;
        Refresh();
    }

    private void Refresh()
    {
        if (World?.IsAuthority != true)
            return;
        if (Read() is not { } radiance)
            return;
        for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
        {
            float scale = i == 0 ? Band0Scale.Value : i < 4 ? Band1Scale.Value : Band2Scale.Value;
            radiance[i] = radiance[i] * scale;
        }
        if (!radiance.Equals(Harmonics.Value))
            Harmonics.Value = radiance;
    }

    private SphericalHarmonicsL2? Read()
    {
        var probe = Probe.Target;
        if (probe != null && probe.UpdateMode.Value == ProbeUpdateMode.FromCubemap && probe.Cubemap.Asset?.Radiance is { } stored)
        {
            _source = "the probe's cubemap";
            for (int i = 0; i < SphericalHarmonicsL2.Count; i++)
                stored[i] = stored[i] * probe.Intensity.Value;
            return stored;
        }
        if (Skybox.ActiveIn(World)?.Radiance() is { } sky)
        {
            _source = probe != null && probe.SkyOnly.Value ? "the sky, which is all the probe captures" : "the sky";
            return sky;
        }
        _source = "nothing yet: no stored cubemap and no sky picture loaded";
        return null;
    }

    public void BuildInspectorBody(UIBuilder ui)
    {
        var average = CubemapHarmonics.Average(Harmonics.Value);
        InspectorStats.AddRow(ui, "Read from", _source);
        InspectorStats.AddRow(ui, "Average light", $"{average.x:0.000} {average.y:0.000} {average.z:0.000} (linear)");
    }
}
