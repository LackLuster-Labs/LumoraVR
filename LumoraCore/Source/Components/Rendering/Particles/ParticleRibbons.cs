// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Simulation.Particles;
using Lumora.Simulation.Particles.Modules;

namespace Lumora.Core.Components;

// Threads live particles onto ribbons in emission order.
//
// A ribbon is not a trail: every point is a LIVE particle, so the whole band moves when the particles
// do. That is what makes it right for a banner, an arc of energy or a stream, and wrong for a streak
// that has to stay where it was drawn - use ParticleTrails for that.
//
// Without a splitter attached, particles keep joining the same ribbon until MaxRibbonLength cuts it.
[ComponentCategory("Rendering/Particles")]
public sealed class ParticleRibbons : ParticleModuleBase
{
    [Group("Capacity")]
    // Published ribbons at once. Past it the OLDEST are dropped.
    public readonly Sync<int> MaxRibbons = new();

    // Particles per ribbon. Reaching it cuts the ribbon.
    public readonly Sync<int> MaxRibbonLength = new();

    // Fraction of particles that join a ribbon at all, 0 to 1.
    public readonly Sync<float> Ratio = new();

    public readonly Sync<StrandDistribution> Distribution = new();

    [Group("Appearance")]
    public readonly Sync<bool> UseParticleColor = new();
    public readonly Sync<bool> UseParticleSize = new();
    public readonly Sync<ParticleSizeAxis> WidthAxis = new();
    public readonly Sync<float> WidthScale = new();
    public readonly Sync<colorHDR> ColorScale = new();

    // Smoothed vertices per control segment. 1 draws the raw polyline through the particles.
    public readonly Sync<int> Smoothing = new();

    [Group("Over Ribbon")]
    // Key positions, 0 at the newest particle in the ribbon to 1 at the oldest.
    public readonly SyncFieldList<float> WidthTimes = new();
    public readonly SyncFieldList<float> WidthValues = new();

    public readonly SyncFieldList<float> ColorTimes = new();
    public readonly SyncFieldList<colorHDR> ColorValues = new();

    [Group("Rendering")]
    public readonly Sync<StrandUVMode> UVMode = new();
    public readonly Sync<float> TileLength = new();
    public readonly Sync<StrandAlignment> Alignment = new();
    public readonly Sync<float3> AlignmentUp = new();

    // Same pair as on ParticleTrails: the ribbon's own material, and the normals a lit one needs.
    public readonly AssetRef<MaterialAsset> Material = new();
    public readonly Sync<bool> GenerateLightingData = new();

    private readonly FloatCurve _widthCurve = new();
    private readonly ColorGradient _colorGradient = new();
    private int _widthRevision = -1;
    private int _colorRevision = -1;

    public override void OnInit()
    {
        base.OnInit();
        MaxRibbons.Value = 64;
        MaxRibbonLength.Value = 64;
        Ratio.Value = 1f;
        UseParticleColor.Value = true;
        UseParticleSize.Value = true;
        WidthAxis.Value = ParticleSizeAxis.Average;
        WidthScale.Value = 1f;
        ColorScale.Value = colorHDR.White;
        Smoothing.Value = 4;
        TileLength.Value = 1f;
        AlignmentUp.Value = float3.Up;
    }

    internal override ParticleSimModule CreateSimModule() => new ParticleRibbonsModule();

    internal override void PushParameters(ParticleSimModule module)
    {
        var ribbons = (ParticleRibbonsModule)module;
        ribbons.MaxRibbons = MaxRibbons.Value;
        ribbons.MaxRibbonLength = MaxRibbonLength.Value;
        ribbons.Ratio = Ratio.Value;
        ribbons.Distribution = Distribution.Value;
        ribbons.UseParticleColor = UseParticleColor.Value;
        ribbons.UseParticleSize = UseParticleSize.Value;
        ribbons.WidthAxis = WidthAxis.Value;
        ribbons.WidthScale = WidthScale.Value;
        ribbons.ColorScale = ColorScale.Value;
        ribbons.Smoothing = Smoothing.Value;
        ribbons.UVMode = UVMode.Value;
        ribbons.TileLength = TileLength.Value;
        ribbons.Alignment = Alignment.Value;
        ribbons.AlignmentUp = AlignmentUp.Value;

        ParticleCurveHelper.Rebuild(WidthTimes, WidthValues, _widthCurve, ref _widthRevision);
        ParticleCurveHelper.RebuildGradient(ColorTimes, ColorValues, _colorGradient, ref _colorRevision);
        ribbons.WidthOverRibbon = _widthCurve;
        ribbons.ColorOverRibbon = _colorGradient;
    }
}
