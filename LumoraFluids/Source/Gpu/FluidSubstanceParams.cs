// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// Turns a substance's physical fields and the spacing into the solver's coefficients. Two numbers come from the
// lattice the spacing makes rather than from a formula: the rest fill (so C = fill / rest - 1 is zero for a calm
// puddle at any spacing; the July CPU attempt's density drift came from guessing it) and the Akinci cohesion scale
// (so a flat surface particle feels about Cohesion x gamma x d of pull, whatever the kernel's normalisation does).
// Both are cached per spacing. -xlinka
public static class FluidSubstanceParams
{
    // XSPH: c = 1 - exp(-k nu / d^2 dt)
    public const float ViscosityFit = 1f;
    // A viscous network must yield under a lift instead of behaving as a solid slab. The small
    // bulk viscosity fit accounts for the finite bond passes; capillary thinning uses the authored viscosity. -xlinka
    public const float GameViscosity = 4f;
    public const float GameViscosityExponent = 1f;
    public const float ViscousModulus = 200f;
    // the strain at which a viscous kind's bond tears when it has no StretchLimit of its own (a splash separates)
    public const float ViscousTear = 2f;
    // from here up a kind without a yield network gets viscous bonds; water stays a liquid
    public const float ViscousNetworkViscosity = 0.05f;
    // extra bond passes (FluidViscous, in pairs) a viscous network gets each substep: one correction pass moves an
    // elastic network's information one bond, so its stiffness grows with the passes squared
    public static int ViscousBondPairs = 2;
    // FluidSubstanceRecord.Flags: goo 1, bonded 2, and 4 for a viscous network, which also takes the tier's full
    // substeps (the bond passes and the substeps together hold a honey blob; neither alone does)
    public const uint FlagStiff = 4;
    // even water gets this much smoothing per second, against particle noise
    public const float MinXsph = 2f;
    // viscous saturation: above this the extra smoothing passes start
    public const float SaturationViscosity = 1f;
    public const float GooSleepSpeed = 0.005f;
    public const float LiquidSleepSpeed = 0.01f;
    // anti-clumping push for goo, so cohesion cannot raisin it
    public const float GooNearRepulsion = 0.3f;
    // bending stiffness per unit viscosity and spacing, for coiling ropes
    public const float BendStiffness = 50f;

    private static readonly object Gate = new();
    private static readonly Dictionary<float, (double RestFill, double CohesionSum)> Lattice = new();

    public static FluidSubstanceRecord Derive(FluidSubstance substance, float spacing)
    {
        if (substance == null)
            return default;
        Span<float> row = stackalloc float[FluidPresetTable.Fields];
        FluidPresetTable.Read(substance, row);
        var c = substance.Tint.Value;
        uint tint = PackTint(c.r, c.g, c.b, c.a);
        return Derive(row, spacing, tint, 0);
    }

    // Row in FluidPresetTable's shape. BondSlots is the tier's; 0 leaves it for the solver to fill.
    public static FluidSubstanceRecord Derive(ReadOnlySpan<float> row, float spacing, uint tint, uint bondSlots)
    {
        float d = spacing > 0f && float.IsFinite(spacing) ? spacing : row[FluidPresetTable.Spacing];
        float h = 2f * d;
        float rho = Math.Max(row[FluidPresetTable.Density], 1f);
        float mu = Math.Max(row[FluidPresetTable.Viscosity], 1e-6f);
        float g = Math.Max(row[FluidPresetTable.Elasticity], 0f);
        float tauY = Math.Max(row[FluidPresetTable.YieldStress], 0f);
        float gamma = Math.Max(row[FluidPresetTable.SurfaceTension], 0f);
        float mass = rho * d * d * d;
        var (restFill, cohesionSum) = LatticeOf(d);
        float stick = Math.Max(row[FluidPresetTable.Stickiness], 0f);
        float anchorStrength = stick * MathF.PI * 0.25f * d * d;
        float stickDistance = Math.Max(row[FluidPresetTable.StickDistance], 0f);
        bool bonded = g > 0f;
        bool goo = bonded || mu >= SaturationViscosity;
        // A yielding network (bonds and a yield stress: slime, glue, mud) carries its viscosity in the bonds' creep,
        // Herschel-Bulkley with creep time mu / G. Smoothing its velocities as well counted mu twice, and so hard
        // (five passes at c 0.8 a substep for slime) that a lifting foot's anchors could not drag their own layer
        // out of the puddle. The other kinds take their viscosity from velocity smoothing.
        bool network = bonded && tauY > 0f;
        bool viscousNet = !network && mu >= ViscousNetworkViscosity;
        float gBond = viscousNet ? Math.Max(g, ViscousModulus) : g;
        float creepViscosity = viscousNet ? GameViscosity * MathF.Pow(mu, GameViscosityExponent) : mu;
        float stretchLimit = viscousNet && !bonded ? ViscousTear : Math.Max(row[FluidPresetTable.StretchLimit], 0.01f);
        bonded = bonded || viscousNet;

        var r = new FluidSubstanceRecord
        {
            Spacing = d,
            SmoothingRadius = h,
            RestDensity = (float)restFill,
            ParticleMass = mass,
            Density = rho,
            Viscosity = mu,
            XsphFactor = network ? MinXsph : Math.Max(ViscosityFit * (mu / rho) / (d * d), MinXsph),
            FlowIndex = Math.Clamp(row[FluidPresetTable.FlowIndex], 0.1f, 4f),
            YieldStrain = bonded ? tauY / gBond : 0f,
            BondCompliance = bonded ? 1f / (gBond * d) : 0f,
            CreepTime = bonded ? creepViscosity / gBond : 0f,
            Relaxation = Math.Max(row[FluidPresetTable.Relaxation], 0f),
            StretchLimit = stretchLimit,
            MinThread = Math.Max(row[FluidPresetTable.MinThread], 1e-6f),
            SurfaceTension = gamma,
            Cohesion = cohesionSum > 0 ? (float)(row[FluidPresetTable.Cohesion] * gamma * d / (mass * (double)mass * cohesionSum)) : 0f,
            Adhesion = Math.Clamp(row[FluidPresetTable.Adhesion], 0f, 1f),
            AnchorStrength = anchorStrength,
            // full strength at StickDistance of pull; with no band at all, a stiff hold
            AnchorCompliance = anchorStrength > 0f ? Math.Max(stickDistance, 1e-4f) / anchorStrength : 0f,
            StickDistance = stickDistance,
            StickTime = Math.Max(row[FluidPresetTable.StickTime], 0f),
            Friction = Math.Clamp(row[FluidPresetTable.Friction], 0f, 1f),
            DryTime = Math.Max(row[FluidPresetTable.DryTime], 0f),
            CureTime = Math.Max(row[FluidPresetTable.CureTime], 0f),
            Washability = Math.Clamp(row[FluidPresetTable.Washability], 0f, 1f),
            ColorMixing = Math.Clamp(row[FluidPresetTable.ColorMixing], 0f, 1f),
            MaxSpeed = Math.Clamp(row[FluidPresetTable.MaxSpeed], 0.1f, 50f),
            SCorrK = 0.1f,
            SCorrN = 4f,
            SCorrDq = 0.2f,
            NearRepulsion = bonded ? GooNearRepulsion : 0f,
            BendCompliance = bonded ? 1f / (mu * d * BendStiffness) : 0f,
            SleepSpeed = goo ? GooSleepSpeed : LiquidSleepSpeed,
            BondRadius = 0.5f * d,
            Tint = tint,
            Flags = (goo ? 1u : 0u) | (bonded ? 2u : 0u) | (viscousNet ? FlagStiff : 0u),
            BondSlots = bondSlots,
            ViscousPasses = network ? 0u : viscousNet ? (uint)ViscousBondPairs : mu >= 20f ? 2u : mu >= SaturationViscosity ? 1u : 0u,
            YieldStress = tauY,
        };
        return r;
    }

    public static float YieldStressOf(in FluidSubstanceRecord record) => record.YieldStress;

    public static uint PackTint(float r, float g, float b, float a)
    {
        static uint C(float v) => (uint)Math.Clamp((int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f), 0, 255);
        return C(r) | (C(g) << 8) | (C(b) << 16) | (C(a) << 24);
    }

    // The fill of a particle inside a cubic lattice at spacing d (h = 2d, every volume d^3, itself included), and
    // the Akinci cohesion sum of a particle on that lattice's flat top face along the face normal.
    public static (double RestFill, double CohesionSum) LatticeOf(float spacing)
    {
        lock (Gate)
        {
            if (Lattice.TryGetValue(spacing, out var cached))
                return cached;
        }
        double d = spacing, h = 2 * d, h2 = h * h, v = d * d * d;
        double poly6 = 315.0 / (64.0 * Math.PI * Math.Pow(h, 9));
        double fill = 0, cohesion = 0;
        int reach = 3;
        for (int z = -reach; z <= reach; z++)
        for (int y = -reach; y <= reach; y++)
        for (int x = -reach; x <= reach; x++)
        {
            double r2 = (x * x + y * y + z * z) * d * d;
            if (r2 < h2)
            {
                double t = h2 - r2;
                fill += v * poly6 * t * t * t;
            }
            if (y < 0 || (x == 0 && y == 0 && z == 0))
                continue;
            // neighbours below a top-face particle at the origin, seen from the particle: x_i - x_j points up
            double r = Math.Sqrt(r2);
            if (r >= h)
                continue;
            if (y == 0)
                continue;
            cohesion += CohesionKernel(r, h) * (y * d / r);
        }
        var result = (fill, cohesion);
        lock (Gate)
            Lattice[spacing] = result;
        return result;
    }

    private static double CohesionKernel(double r, double h)
    {
        if (r >= h || r <= 0)
            return 0;
        double h3 = h * h * h;
        double c = 32.0 / (Math.PI * h3 * h3 * h3);
        double a = (h - r) * (h - r) * (h - r) * r * r * r;
        return r > 0.5 * h ? c * a : c * (2 * a - h3 * h3 / 64.0);
    }

    // A particle's surface normal on a calm flat face, h times the sum of V grad W over the half lattice under it:
    // what the curvature term compares neighbours with. Exposed for tests.
    public static float3 FaceNormal(float spacing)
    {
        double d = spacing, h = 2 * d, v = d * d * d;
        double spiky = -45.0 / (Math.PI * Math.Pow(h, 6));
        double nx = 0, ny = 0, nz = 0;
        for (int z = -3; z <= 3; z++)
        for (int y = -3; y <= 0; y++)
        for (int x = -3; x <= 3; x++)
        {
            if (x == 0 && y == 0 && z == 0)
                continue;
            double rx = -x * d, ry = -y * d, rz = -z * d;
            double r = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (r >= h)
                continue;
            double s = spiky * (h - r) * (h - r) / r * v;
            nx += s * rx;
            ny += s * ry;
            nz += s * rz;
        }
        return new float3((float)(h * nx), (float)(h * ny), (float)(h * nz));
    }
}
