// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// The solver's coefficients for one substance, derived on the CPU from the substance's physical fields and the
// spacing, uploaded only when a field changes. Lengths in m, times in s.
// std430, 256 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 256)]
public struct FluidSubstanceRecord
{
    public const int Size = 256;

    // d
    public float Spacing;
    // h = 2d
    public float SmoothingRadius;
    // lattice calibrated
    public float RestDensity;
    // kg
    public float ParticleMass;
    // kg/m3
    public float Density;
    // Pa s
    public float Viscosity;
    public float XsphFactor;
    public float FlowIndex;
    public float YieldStrain;
    public float BondCompliance;
    public float CreepTime;
    public float Relaxation;
    public float StretchLimit;
    // m
    public float MinThread;
    // N/m
    public float SurfaceTension;
    public float Cohesion;
    public float Adhesion;
    // N
    public float AnchorStrength;
    // m/N
    public float AnchorCompliance;
    // m
    public float StickDistance;
    public float StickTime;
    public float Friction;
    public float DryTime;
    public float CureTime;
    public float Washability;
    public float ColorMixing;
    // m/s
    public float MaxSpeed;
    public float SCorrK;
    public float SCorrN;
    public float SCorrDq;
    public float NearRepulsion;
    public float BendCompliance;
    // m/s
    public float SleepSpeed;
    // m
    public float BondRadius;
    // RGBA8
    public uint Tint;
    public uint Flags;
    public uint BondSlots;
    public uint ViscousPasses;
    // Pa
    public float YieldStress;
}
