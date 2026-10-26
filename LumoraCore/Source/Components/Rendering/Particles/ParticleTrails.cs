// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Assets;
using Lumora.Core.Math;
using Lumora.Simulation.Particles;
using Lumora.Simulation.Particles.Modules;

namespace Lumora.Core.Components;

// Leaves a streak behind every particle.
//
// The trail records where the particle HAS BEEN, so it stays put in the world once laid: sparks that
// arc, a rocket's smoke, a blade trail. Points are committed as the particle pulls MinVertexDistance
// clear of the last one and expire on MaxPointAge, independently of the particle's own lifetime.
//
// Trails cost a fixed MaxTrails x MaxPointsPerTrail block of memory up front and nothing per frame
// after that. Changing either of those two fields RESETS every live trail, so they are setup values,
// not something to drive. -xlinka
[ComponentCategory("Rendering/Particles")]
public sealed class ParticleTrails : ParticleModuleBase
{
    [Group("Capacity")]
    // Live trails at once. Particles past it get none.
    public readonly Sync<int> MaxTrails = new();

    // Points one trail can hold. The oldest is dropped past it.
    public readonly Sync<int> MaxPointsPerTrail = new();

    // Fraction of particles that get a trail at all, 0 to 1.
    public readonly Sync<float> Ratio = new();

    public readonly Sync<StrandDistribution> Distribution = new();

    [Group("Shape")]
    // Metres the particle must travel before another point is committed.
    public readonly Sync<float> MinVertexDistance = new();

    // Seconds a point survives after being laid.
    public readonly Sync<float> MaxPointAge = new();

    // World diameter multiplier on every point.
    public readonly Sync<float> WidthScale = new();

    // Smoothed vertices the renderer should draw per control segment. 1 draws the raw polyline.
    public readonly Sync<int> Smoothing = new();

    [Group("Death")]
    // True cuts the trail the moment its particle dies.
    public readonly Sync<bool> DieWithParticle = new();

    // Seconds an orphaned trail takes to fade out. 0 leaves it to age out on MaxPointAge instead.
    public readonly Sync<float> DeathFadeTime = new();

    [Group("Inheritance")]
    public readonly Sync<TrailInheritance> ColorInheritance = new();
    public readonly Sync<TrailInheritance> WidthInheritance = new();

    // Which component of the particle's size becomes the trail width.
    public readonly Sync<ParticleSizeAxis> WidthAxis = new();

    public readonly Sync<colorHDR> ColorScale = new();

    [Group("Over Trail")]
    // Key positions, 0 at the particle end to 1 at the far, oldest end.
    public readonly SyncFieldList<float> WidthTimes = new();
    public readonly SyncFieldList<float> WidthValues = new();

    public readonly SyncFieldList<float> ColorTimes = new();
    public readonly SyncFieldList<colorHDR> ColorValues = new();

    [Group("Rendering")]
    public readonly Sync<StrandUVMode> UVMode = new();

    // World length of one texture repeat under TilePerSegment.
    public readonly Sync<float> TileLength = new();

    public readonly Sync<StrandAlignment> Alignment = new();

    // Reference up axis for VelocityAligned, in the system's simulation space.
    public readonly Sync<float3> AlignmentUp = new();

    // Draws the trails with this material in place of the built-in strand shader, as the system's own
    // Material does for its particles. The strand's colour still rides in on the vertex colours.
    public readonly AssetRef<MaterialAsset> Material = new();

    // Normals and tangents on the trail mesh. A lit material needs them to shade the band at all; the
    // built-in shader and unlit materials read neither, so they are left off unless asked for.
    public readonly Sync<bool> GenerateLightingData = new();

    private readonly FloatCurve _widthCurve = new();
    private readonly ColorGradient _colorGradient = new();
    private int _widthRevision = -1;
    private int _colorRevision = -1;

    public override void OnInit()
    {
        base.OnInit();
        MaxTrails.Value = 256;
        MaxPointsPerTrail.Value = 48;
        Ratio.Value = 1f;
        MinVertexDistance.Value = 0.02f;
        MaxPointAge.Value = 0.6f;
        WidthScale.Value = 1f;
        Smoothing.Value = 4;
        DeathFadeTime.Value = 0.25f;
        ColorInheritance.Value = TrailInheritance.Continuous;
        WidthInheritance.Value = TrailInheritance.Birth;
        WidthAxis.Value = ParticleSizeAxis.Average;
        ColorScale.Value = colorHDR.White;
        TileLength.Value = 1f;
        AlignmentUp.Value = float3.Up;
    }

    internal override ParticleSimModule CreateSimModule() => new ParticleTrailsModule();

    internal override void PushParameters(ParticleSimModule module)
    {
        var trails = (ParticleTrailsModule)module;
        trails.MaxTrails = MaxTrails.Value;
        trails.MaxPointsPerTrail = MaxPointsPerTrail.Value;
        trails.Ratio = Ratio.Value;
        trails.Distribution = Distribution.Value;
        trails.MinVertexDistance = MinVertexDistance.Value;
        trails.MaxPointAge = MaxPointAge.Value;
        trails.WidthScale = WidthScale.Value;
        trails.Smoothing = Smoothing.Value;
        trails.DieWithParticle = DieWithParticle.Value;
        trails.DeathFadeTime = DeathFadeTime.Value;
        trails.ColorInheritance = ColorInheritance.Value;
        trails.WidthInheritance = WidthInheritance.Value;
        trails.WidthAxis = WidthAxis.Value;
        trails.ColorScale = ColorScale.Value;
        trails.UVMode = UVMode.Value;
        trails.TileLength = TileLength.Value;
        trails.Alignment = Alignment.Value;
        trails.AlignmentUp = AlignmentUp.Value;

        ParticleCurveHelper.Rebuild(WidthTimes, WidthValues, _widthCurve, ref _widthRevision);
        ParticleCurveHelper.RebuildGradient(ColorTimes, ColorValues, _colorGradient, ref _colorRevision);
        trails.WidthOverTrail = _widthCurve;
        trails.ColorOverTrail = _colorGradient;
    }
}
