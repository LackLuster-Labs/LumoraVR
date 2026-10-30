// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// Solver constants that are not a substance's, and the scratch layouts the kernels and the C# side both size.
// FluidKernels.glsli carries each with the same value as a FLUID_ define; change both or neither. -xlinka
public static class FluidConstants
{
    // threads per particle in the gathering kernels
    public const int Lanes = 8;
    // FluidViscous's Pass bit for a position pass over the bonds (FLUID_PASS_POSITIONS)
    public const uint PassPositions = 0x100;
    public const int ScanBlock = 1024;
    public const int ScanThreads = 256;
    // the smallest hash table the sort uses: a multiple of the scan block keeps the awake and dead starts on block edges
    public const int MinGridTable = 1024;
    public const int MaxScanBlocks = 512;
    public const int RegionTable = 4096;
    public const int MaxSystems = 16;
    public const int MaxPeople = 32;

    // per slot scratch, vec4 each: lambda/fill/list/push, contact plane, surface velocity/key, surface factors
    public const int SlotVec4s = 4;
    // per id aux, vec4 each: surface normal + density neighbours, vorticity
    public const int AuxVec4s = 2;
    public const int ByIdWords = 8;
    public const int CounterWords = 64;
    public const int StatsWords = 6672;
    // anchor and refine scans, the anchor list, then the ligament table (8 words a ligament, 1024 of them)
    public const int CandidateWords = 4 * ChunksMax + 16 + 8 * 1024 + 8 * 1024;
    // Cover every particle addressable by the neighbour buffer; refinement must not impose a smaller pool. -xlinka
    public const int ChunksMax = (int.MaxValue / (FluidLayout.MaxNeighbours * 4) + FluidLayout.GroupSize - 1) / FluidLayout.GroupSize;

    // Indirect dispatch args, a buffer of their own (FluidKernels.glsli says why).
    public const int ArgsLive = 0;
    public const int ArgsAwake = 3;
    public const int ArgsAwakeLanes = 6;
    public const int ArgsWords = 16;

    // Counters; the first 16 words are unused.
    public const int CounterLive = 16;
    public const int CounterAwake = 17;
    public const int CounterPresort = 18;
    public const int CounterFree = 19;
    public const int CounterFreeBase = 20;
    public const int CounterDead = 21;
    public const int CounterPool = 22;
    public const int CounterSpawned = 23;
    public const int CounterGrow = 24;
    public const int CounterRefineBase = 25;
    public const int CounterFrame = 28;


    // Particle phase flags above the layout's.
    public const uint ParticleRefined = 0x100000;
    public const uint ParticleDying = 0x200000;

    // Bond StrainFlags flags; the strain is a half float in the low 16 bits. BondStrand is the link the surface draws
    // as a tube (its FLUID_BOND_CHAIN).
    public const uint BondStrand = 0x10000;
    public const uint BondElasto = 0x20000;
    // Frame end A chooses one stored row to own the link through B and D. -xlinka
    public const uint BondOwner = 0x80000;
    public const uint AnchorFrozen = 16;

    // neighbour list radius past h, as a fraction of h
    public const float Skin = 0.3f;
    // a hitch longer than this slows the fluid down, it never breaks it
    public const float MaxFrameSeconds = 1f / 30f;
    public const int MinSubsteps = 2;
    // substeps from the speed: a particle crosses at most this share of h per substep
    public const float SubstepTravel = 0.4f;
    // the anchor and link maths assume at least these spare slots for a refined particle's two links
    public const int MinBondSlots = 2;
}
