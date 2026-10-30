// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// Constants the C# packers and the GLSL kernels share. FluidLayout.glsli carries each one as a FLUID_ define
// with the same value; change both or neither.
public static class FluidLayout
{
    // workgroup width of every particle kernel
    public const int GroupSize = 64;
    // neighbour list cap, h + skin
    public const int MaxNeighbours = 96;
    // empty index or id
    public const uint None = 0xFFFFFFFF;
    // the accumulators' fixed point
    public const float NanolitresPerCubicMetre = 1e12f;
    public const int MaxSubstances = 16;
    public const int MaxColliders = 512;
    public const int MaxImpulseBodies = 256;
    // FluidForceRecords a frame (est.)
    public const int MaxForces = 32;
    public const int BodyParts = 16;
    public const int Limbs = 4;
    public const uint PhaseMask = 15;
    public const uint PhaseBulk = 0;
    public const uint PhaseStrand = 1;
    public const uint PhaseDrop = 2;
    public const uint PhaseSpray = 3;
    // a free slot
    public const uint PhaseDead = 15;
    public const uint ParticleAsleep = 16;
    public const uint ParticleAnchored = 32;
    public const uint ParticleFading = 64;
    public const uint ParticleNew = 128;
    // 8 bits
    public const int NeighbourCountShift = 8;
    // 2 bits
    public const int ChainBondShift = 16;
    public const int SubstanceShift = 0;
    public const int SystemShift = 8;
    public const int FrameShift = 16;
    public const int OriginShift = 24;
    public const uint ByteMask = 255;
    public const uint AnchorKindMask = 15;
    public const int AnchorAgeShift = 8;
    public const uint AnchorNone = 0;
    public const uint AnchorStatic = 1;
    public const uint AnchorRigid = 2;
    public const uint AnchorBone = 3;
    public const uint AnchorSkin = 4;
    public const uint ColliderBox = 0;
    public const uint ColliderSphere = 1;
    public const uint ColliderCapsule = 2;
    public const uint ColliderCylinder = 3;
    public const uint ColliderConvex = 4;
    public const uint ColliderMesh = 5;
    public const uint ProxyCapsule = 0;
    public const uint ProxyBox = 1;
    public const uint ProxySphere = 2;
    // FluidStickMode
    public const uint PersonStickMask = 3;
    // 3 bits, view LOD
    public const int PersonLodShift = 2;
    public const uint PersonLocal = 32;
    public const uint PersonViewOnly = 64;
    // Hidden or StandIn: anchors freeze
    public const uint PersonFrozen = 128;
    // teleport, avatar swap
    public const uint PersonDropAnchors = 0x100;
    // FluidContactVertex.BoneN: the bone, relative to its person
    public const uint ContactBoneMask = 0xFFFF;
    // FluidContactVertex.Bone0 bits 16-23: the vertex's FluidBodyParts index
    public const int ContactPartShift = 16;
    // a person's relative bones count the part bones first, so skin bones start here
    public const uint SkinBoneFirst = 16;
    // FluidContactRecord.Part bits 8-15: the deepest contact's frame substance
    public const int ContactSubstanceShift = 8;
    // FluidContactVertex.Bone0 bits 24-31: the vertex's surface flags
    public const int ContactFlagShift = 24;
    // Bone0 flag: an open surface (a hair card, a hem), collided from the side the fluid came from
    public const uint ContactTwoSided = 0x1000000;
    // this machine keeps at least one system here
    public const uint FrameKeeper = 1;
    // bake candidates are collected this frame
    public const uint FrameBakes = 2;
    public const uint FrameFocused = 4;
    public const uint FrameBackground = 8;
    // uploads and commands apply; clear on a repeated sub-frame of one block
    public const uint FrameFirstOfBlock = 16;
    // the keeper bins into the ledger
    public const uint FrameBin = 32;
    // a machine that does not keep compares its particles with the ledger
    public const uint FrameReconcile = 64;
    public const uint FrameLite = 128;
    public const uint FrameWaterOneIteration = 0x100;
    public const uint FrameRemoteRefineOff = 0x200;
    public const uint FrameHalfRateBricks = 0x400;
    public const uint FrameNoSurface = 0x800;
    // a paused world: apply the commands, run no substep
    public const uint FrameCommandsOnly = 0x1000;
    public const uint SystemKept = 1;
    public const uint SystemPaused = 2;
    public const uint SystemContainer = 4;
    public const uint SystemStickToPeople = 8;
    public const uint SystemPushBodies = 16;
    public const uint SystemMixColours = 32;
    public const uint SystemCollideWithWorld = 64;
    // a container's frame; Container holds its index
    public const uint FrameRecordContainer = 1;
    // under a rigid body: pushed back through the impulse table
    public const uint ColliderDynamic = 1;
    public const uint ColliderTrigger = 2;
    // moved more than a millimetre since last frame
    public const uint ColliderMoved = 4;
    public const uint ColliderDrain = 8;
    // fluid touches it and never anchors to it
    public const uint ColliderNoStick = 16;
    public const uint ForceDirectional = 0;
    public const uint ForceRadial = 1;
    public const uint ForceVortex = 2;
    public const uint ForceDrag = 3;
    public const uint SummaryAllAsleep = 1;
    public const uint SummaryPoolEmpty = 2;
    public const uint SummaryNanReset = 4;
    public const uint SummaryEnergyRise = 8;

    // The summary read back each frame: a header, then each section at a fixed offset, sized by its capacity.
    // One async read of SummaryBytes a frame, which stays under 16 KB.
    public const int SummaryContactsCapacity = 64;
    public const int SummaryContactsOffset = 128;
    public const int SummaryLimbsCapacity = 32;
    public const int SummaryLimbsOffset = 1152;
    public const int SummaryBakesCapacity = 32;
    public const int SummaryBakesOffset = 2176;
    public const int SummaryPendingCapacity = 64;
    public const int SummaryPendingOffset = 3200;
    public const int SummaryImpulsesCapacity = 32;
    public const int SummaryImpulsesOffset = 4224;
    public const int SummaryVolumesCapacity = 16;
    public const int SummaryVolumesOffset = 5248;
    public const int SummarySystemsCapacity = 16;
    public const int SummarySystemsOffset = 5504;
    public const int SummaryQueriesCapacity = 128;
    public const int SummaryQueriesOffset = 6528;
    public const int SummaryPropsCapacity = 32;
    public const int SummaryPropsOffset = 8576;
    public const int SummaryBinsCapacity = 192;
    public const int SummaryBinsOffset = 9600;
    public const int SummaryBytes = 15744;
}
