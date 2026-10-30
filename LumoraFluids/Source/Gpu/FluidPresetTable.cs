// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// The presets' physical values, in SI units (lengths in metres here; the component shows MinThread and StickDistance
// in mm). Spacing is the High tier's. A row is a float[] indexed by the Field constants, the same shape
// FluidSubstanceParams derives the solver's coefficients from, so a test can build a substance without a world.
// -xlinka
public static class FluidPresetTable
{
    public const int Spacing = 0;
    public const int Density = 1;
    public const int Viscosity = 2;
    public const int FlowIndex = 3;
    public const int YieldStress = 4;
    public const int Elasticity = 5;
    public const int Relaxation = 6;
    public const int StretchLimit = 7;
    public const int MinThread = 8;
    public const int SurfaceTension = 9;
    public const int Cohesion = 10;
    public const int Adhesion = 11;
    public const int Stickiness = 12;
    public const int StickDistance = 13;
    public const int StickTime = 14;
    public const int DryTime = 15;
    public const int CureTime = 16;
    public const int Washability = 17;
    public const int ColorMixing = 18;
    public const int Traction = 19;
    public const int FootGrip = 20;
    public const int MaxSpeed = 21;
    // not a preset column: the component's own dial, which picking a preset leaves alone
    public const int Friction = 22;
    public const int Fields = 23;

    public const float DefaultFriction = 0.2f;

    // Where the table says n/a: no strands means no tear (the slider's top), no stickiness means no stick time, and
    // water washes out completely. Water dries in 80 s, not 2.4's 900: by the lead's call a water coat is gone in
    // 2-5 minutes, and at 80 s a 1 mL coat is under 0.05 mL in 4.
    private static readonly float[][] Rows =
    {
        // Custom starts from water
        new[] { 0.016f, 1000f, 0.001f, 1f, 0f, 0f, 0f, 30f, 0.0005f, 0.072f, 1.0f, 0.6f, 0f, 0.001f, 0f, 80f, 0f, 1f, 1.0f, 1.0f, 0f, 6f, DefaultFriction },
        // Water
        new[] { 0.016f, 1000f, 0.001f, 1f, 0f, 0f, 0f, 30f, 0.0005f, 0.072f, 1.0f, 0.6f, 0f, 0.001f, 0f, 80f, 0f, 1f, 1.0f, 1.0f, 0f, 6f, DefaultFriction },
        // Oil
        new[] { 0.014f, 920f, 0.08f, 1f, 0f, 0f, 0f, 30f, 0.0004f, 0.032f, 0.8f, 0.9f, 20f, 0.0015f, 0.1f, 0f, 0f, 0.2f, 0.2f, 0.5f, 0f, 4f, DefaultFriction },
        // Paint
        new[] { 0.010f, 1300f, 0.5f, 0.6f, 5f, 0f, 0f, 30f, 0.0003f, 0.035f, 0.8f, 0.95f, 60f, 0.002f, 0.05f, 600f, 300f, 0.5f, 0.5f, 0.9f, 0.05f, 3f, DefaultFriction },
        // Honey
        new[] { 0.010f, 1420f, 10f, 1f, 0f, 50f, 0f, 30f, 0.00015f, 0.06f, 1.0f, 0.8f, 900f, 0.002f, 0.15f, 3600f, 0f, 0.9f, 0.2f, 0.7f, 0.2f, 2.5f, DefaultFriction },
        // Slime
        new[] { 0.010f, 1050f, 30f, 0.5f, 40f, 400f, 0.06f, 8f, 0.00025f, 0.05f, 1.5f, 0.7f, 600f, 0.003f, 0.1f, 1800f, 0f, 0.6f, 0.2f, 0.6f, 0.35f, 2.5f, DefaultFriction },
        // Glue
        new[] { 0.010f, 1100f, 50f, 0.8f, 20f, 800f, 0.15f, 12f, 0.0002f, 0.04f, 2.0f, 1.0f, 1500f, 0.003f, 0.2f, 600f, 240f, 0.1f, 0.2f, 0.4f, 0.6f, 2.5f, DefaultFriction },
        // Mud
        new[] { 0.012f, 1700f, 20f, 0.4f, 150f, 2000f, 0f, 0.5f, 0.001f, 0.06f, 0.5f, 0.6f, 150f, 0.002f, 0.2f, 1200f, 0f, 0.8f, 0.2f, 0.5f, 0.3f, 3f, DefaultFriction },
    };

    // Each preset's colour, which Apply writes into the substance's Tint: the renderer's FluidMaterial.Tint defaults to
    // white and multiplies, so the colour comes from here. Only rgb is drawn. In FluidPreset order.
    private static readonly colorHDR[] Tints =
    {
        // Custom starts from water
        new(0.85f, 0.93f, 1.00f, 1f),
        // Water: clear with a faint blue
        new(0.85f, 0.93f, 1.00f, 1f),
        // Oil: dark amber
        new(0.30f, 0.17f, 0.03f, 1f),
        // Paint: a bright red to start from
        new(0.90f, 0.14f, 0.11f, 1f),
        // Honey: amber
        new(0.85f, 0.50f, 0.08f, 1f),
        // Slime: green
        new(0.35f, 0.85f, 0.20f, 1f),
        // Glue: off-white
        new(0.93f, 0.92f, 0.86f, 1f),
        // Mud: brown
        new(0.33f, 0.22f, 0.13f, 1f),
    };

    public static colorHDR TintOf(FluidPreset preset)
    {
        int index = (int)preset;
        return index >= 0 && index < Tints.Length ? Tints[index] : Tints[(int)FluidPreset.Water];
    }

    public static float[] Values(FluidPreset preset)
    {
        int index = (int)preset;
        if (index < 0 || index >= Rows.Length)
            index = (int)FluidPreset.Water;
        return (float[])Rows[index].Clone();
    }

    public static void Apply(FluidSubstance substance, FluidPreset preset)
    {
        if (substance == null)
            return;
        var v = Values(preset);
        substance.Kind.Value = preset;
        substance.Spacing.Value = v[Spacing];
        substance.Density.Value = v[Density];
        substance.Viscosity.Value = v[Viscosity];
        substance.FlowIndex.Value = v[FlowIndex];
        substance.YieldStress.Value = v[YieldStress];
        substance.Elasticity.Value = v[Elasticity];
        substance.Relaxation.Value = v[Relaxation];
        substance.StretchLimit.Value = v[StretchLimit];
        substance.MinThread.Value = v[MinThread] * 1000f;
        substance.SurfaceTension.Value = v[SurfaceTension];
        substance.Cohesion.Value = v[Cohesion];
        substance.Adhesion.Value = v[Adhesion];
        substance.Stickiness.Value = v[Stickiness];
        substance.StickDistance.Value = v[StickDistance] * 1000f;
        substance.StickTime.Value = v[StickTime];
        substance.DryTime.Value = v[DryTime];
        substance.CureTime.Value = v[CureTime];
        substance.Washability.Value = v[Washability];
        substance.ColorMixing.Value = v[ColorMixing];
        substance.Traction.Value = v[Traction];
        substance.FootGrip.Value = v[FootGrip];
        substance.MaxSpeed.Value = v[MaxSpeed];
        substance.Tint.Value = TintOf(preset);
    }

    // The component's current fields as a row, in the same units as the table.
    public static void Read(FluidSubstance substance, Span<float> row)
    {
        if (row.Length < Fields)
            throw new ArgumentException("row too short", nameof(row));
        row[Spacing] = substance.Spacing.Value;
        row[Density] = substance.Density.Value;
        row[Viscosity] = substance.Viscosity.Value;
        row[FlowIndex] = substance.FlowIndex.Value;
        row[YieldStress] = substance.YieldStress.Value;
        row[Elasticity] = substance.Elasticity.Value;
        row[Relaxation] = substance.Relaxation.Value;
        row[StretchLimit] = substance.StretchLimit.Value;
        row[MinThread] = substance.MinThread.Value * 0.001f;
        row[SurfaceTension] = substance.SurfaceTension.Value;
        row[Cohesion] = substance.Cohesion.Value;
        row[Adhesion] = substance.Adhesion.Value;
        row[Stickiness] = substance.Stickiness.Value;
        row[StickDistance] = substance.StickDistance.Value * 0.001f;
        row[StickTime] = substance.StickTime.Value;
        row[DryTime] = substance.DryTime.Value;
        row[CureTime] = substance.CureTime.Value;
        row[Washability] = substance.Washability.Value;
        row[ColorMixing] = substance.ColorMixing.Value;
        row[Traction] = substance.Traction.Value;
        row[FootGrip] = substance.FootGrip.Value;
        row[MaxSpeed] = substance.MaxSpeed.Value;
        row[Friction] = substance.Friction.Value;
    }
}
