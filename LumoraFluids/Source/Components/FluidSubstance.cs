// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core;
using Lumora.Core.Assets;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A fluid's material in physical units, with log sliders where the range spans decades. The solver derives its own
// coefficients from these and the spacing (FluidSubstanceParams), so an author says what a substance is, never how
// the solver runs it. Picking a preset writes every field; moving any dial afterwards reads as Custom. A substance
// starts its world's fluid space like any fluid part, and an edit re-derives the solver's records. -xlinka
[ComponentCategory("Physics/Fluids")]
public sealed class FluidSubstance : Component
{
    public readonly Sync<FluidPreset> Kind = new(FluidPreset.Water);
    public readonly Sync<colorHDR> Tint = new(new colorHDR(0.55f, 0.75f, 0.95f, 1f));

    [Group("Flow")]
    // m, at the High tier
    [Range(0.004f, 0.04f, "0.000")]
    public readonly Sync<float> Spacing = new(0.016f);
    // kg/m3
    [Range(300f, 3000f, "0")]
    public readonly Sync<float> Density = new(1000f);
    // Pa s
    [Range(0.001f, 1000f, "0.000", exponential: true)]
    public readonly Sync<float> Viscosity = new(0.001f);
    // below 1 thins under shear, above 1 thickens
    [Range(0.3f, 2f, "0.00")]
    public readonly Sync<float> FlowIndex = new(1f);
    // Pa; it does not flow below this
    [Range(0f, 500f, "0.0")]
    public readonly Sync<float> YieldStress = new(0f);

    [Group("Stretch")]
    // Pa, shear modulus; 0 = no bonds
    [Range(0f, 5000f, "0", exponential: true)]
    public readonly Sync<float> Elasticity = new(0f);
    // s, polymer relaxation time; 0 = Newtonian. The one dial that sets how long strands get.
    [Range(0f, 5f, "0.000", exponential: true)]
    public readonly Sync<float> Relaxation = new(0f);
    // strain at which a bond tears
    [Range(0.2f, 30f, "0.0")]
    public readonly Sync<float> StretchLimit = new(30f);
    // mm, radius at which a thread pinches
    [Range(0.1f, 3f, "0.00")]
    public readonly Sync<float> MinThread = new(0.5f);

    [Group("Surface")]
    // N/m
    [Range(0f, 0.1f, "0.000")]
    public readonly Sync<float> SurfaceTension = new(0.072f);
    [Range(0f, 3f, "0.00")]
    public readonly Sync<float> Cohesion = new(1f);
    // 0 beads up, 1 spreads
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Adhesion = new(0.6f);

    [Group("Sticking")]
    // Pa, tensile stress an anchor holds
    [Range(0f, 5000f, "0", exponential: true)]
    public readonly Sync<float> Stickiness = new(0f);
    // mm, band in which it grabs a surface
    [Range(0f, 10f, "0.0")]
    public readonly Sync<float> StickDistance = new(1f);
    // s, wetting time to full grip
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> StickTime = new(0f);
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Friction = new(0.2f);

    [Group("Ageing")]
    // s, e-fold time of drying; 0 = never
    public readonly Sync<float> DryTime = new(900f);
    // s, time to set; 0 = never
    public readonly Sync<float> CureTime = new(0f);
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Washability = new(1f);
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> ColorMixing = new(1f);

    [Group("Feel")]
    // walking speed kept in it
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Traction = new(1f);
    // how much a stuck foot holds you back
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> FootGrip = new(0f);
    // m/s
    [Range(1f, 8f, "0.0")]
    public readonly Sync<float> MaxSpeed = new(6f);

    [Group("Sound")]
    public readonly AssetRef<AudioClip> SplashSound = new();
    public readonly AssetRef<AudioClip> SquelchSound = new();
    public readonly AssetRef<AudioClip> SnapSound = new();
    public readonly AssetRef<AudioClip> DripSound = new();
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> SoundVolume = new(1f);

    private bool _applyingPreset;

    public override void OnStart()
    {
        base.OnStart();
        FluidSpace.Ensure(World);
    }

    public override void OnChanges()
    {
        base.OnChanges();
        if (FluidSpace.Find(World) is { } space)
            space.SubstancesDirty = true;
    }

    // Moving any dial after a preset reads as Custom. Only this machine's own edits flip it: a preset another machine
    // picked arrives as its fields and its kind together, and must not be turned into Custom on the way in.
    protected override void OnSyncMemberChanged(IChangeable member)
    {
        base.OnSyncMemberChanged(member);
        if (_applyingPreset || !IsStarted)
            return;
        // Inspector preset selection must apply its fields; received and loaded fields stay authoritative. -xlinka
        if (ReferenceEquals(member, Kind))
        {
            if (!Kind.IsLoading && !Kind.IsInInitPhase)
                UsePreset(Kind.Value);
            return;
        }
        if (Kind.Value == FluidPreset.Custom)
            return;
        if (member is Sync<float> field && !field.IsLoading && !field.IsInInitPhase && IsPhysical(field))
            Kind.Value = FluidPreset.Custom;
    }

    public static string PresetName(FluidPreset preset) => preset.ToString();

    // Writes every field of the preset and names it.
    public void UsePreset(FluidPreset preset)
    {
        _applyingPreset = true;
        try
        {
            if (preset != FluidPreset.Custom)
                FluidPresetTable.Apply(this, preset);
            Kind.Value = preset;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private bool IsPhysical(Sync<float> field)
        => !ReferenceEquals(field, SoundVolume);
}
