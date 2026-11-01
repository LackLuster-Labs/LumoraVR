// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Globalization;
using Lumora.Core;

namespace Lumora.Fluids;

// This machine's measured GPU, kept in the Settings store under Modules.Fluids.Calibration and never sent to the
// account: it describes the card in this box, not the person. The 0.3 s run that fills it times the real chain on a
// synthetic scene during the first fluid world's load hold, and again whenever the card or its driver changes.
//
// Auto picks the highest tier whose awake cap x max substeps x measured ns per particle-substep, plus its dispatch
// budget x the measured floor per dependent dispatch, fits that tier's simulation budget. A tier names a device class
// as a first guess; this is what puts a weak discrete card on Medium and a strong one on Ultra. -xlinka
public sealed class FluidCalibration
{
    // Length of the timed run. Short enough to hide in a load hold, long enough for the card to leave its idle clock.
    public const double RunSeconds = 0.3;

    public FluidCalibration(string gpu, string driver, double nanosecondsPerParticleSubstep, double dispatchFloorMicroseconds, int smallPathCrossover)
    {
        Gpu = Clean(gpu);
        Driver = Clean(driver);
        NanosecondsPerParticleSubstep = nanosecondsPerParticleSubstep;
        DispatchFloorMicroseconds = dispatchFloorMicroseconds;
        SmallPathCrossover = Math.Max(0, smallPathCrossover);
    }

    public string Gpu { get; }

    public string Driver { get; }

    public double NanosecondsPerParticleSubstep { get; }

    public double DispatchFloorMicroseconds { get; }

    // Awake particles below which the one-workgroup path wins; 0 when it never does (every desktop card measured).
    public int SmallPathCrossover { get; }

    public bool IsUsable => NanosecondsPerParticleSubstep > 0 && double.IsFinite(NanosecondsPerParticleSubstep)
        && DispatchFloorMicroseconds >= 0 && double.IsFinite(DispatchFloorMicroseconds);

    public bool Matches(string gpu, string driver)
        => string.Equals(Gpu, Clean(gpu), StringComparison.Ordinal) && string.Equals(Driver, Clean(driver), StringComparison.Ordinal);

    // What the chain would cost at a tier's caps, in ms of GPU.
    public double CostMs(FluidGpuTier tier)
        => FluidQuality.AwakeCap(tier) * (double)FluidQuality.MaxSubsteps(tier) * NanosecondsPerParticleSubstep * 1e-6
           + FluidQuality.DispatchBudget(tier) * DispatchFloorMicroseconds * 1e-3;

    public FluidGpuTier PickTier()
    {
        if (!IsUsable)
            return FluidGpuTier.Low;
        for (var tier = FluidGpuTier.Ultra; tier > FluidGpuTier.Low; tier--)
        {
            if (CostMs(tier) <= FluidQuality.SimulationBudgetMs(tier))
                return tier;
        }
        return FluidGpuTier.Low;
    }

    public string Format()
        => string.Join("|",
            Gpu,
            Driver,
            NanosecondsPerParticleSubstep.ToString("R", CultureInfo.InvariantCulture),
            DispatchFloorMicroseconds.ToString("R", CultureInfo.InvariantCulture),
            SmallPathCrossover.ToString(CultureInfo.InvariantCulture));

    public static bool TryParse(string? text, out FluidCalibration calibration)
    {
        calibration = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split('|');
        if (parts.Length < 5
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ns)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double floor)
            || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int crossover))
            return false;
        calibration = new FluidCalibration(parts[0], parts[1], ns, floor, crossover);
        return calibration.IsUsable;
    }

    public static FluidCalibration? Load()
        => TryParse(Settings.ReadValue(FluidSettingKeys.Calibration, string.Empty), out var calibration) ? calibration : null;

    // The stored calibration when it was taken on this card and driver; anything else is a different machine as far
    // as the numbers go.
    public static FluidCalibration? LoadFor(string gpu, string driver)
        => Load() is { } stored && stored.Matches(gpu, driver) ? stored : null;

    public void Save() => Settings.WriteValue(FluidSettingKeys.Calibration, Format());

    // The separator cannot appear inside a field, and an empty name still has to round-trip.
    private static string Clean(string? text)
    {
        var value = (text ?? string.Empty).Replace('|', '/').Trim();
        return value.Length == 0 ? "unknown" : value;
    }

    public override string ToString()
        => $"{Gpu} ({Driver}): {NanosecondsPerParticleSubstep:0.##} ns per particle-substep, {DispatchFloorMicroseconds:0.##} us per dispatch";
}
