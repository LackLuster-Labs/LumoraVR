// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Lumora.Core.Math;

namespace Lumora.Fluids;

// A contact mesh vertex at rest, uploaded once per equip and skinned on the GPU every frame with the same four
// weights the drawn avatar uses. Its bones are relative to its person, because the bone list is rebuilt every frame
// and a person's ranges move: the low 16 bits of each BoneN (FLUID_CONTACT_BONE_MASK) count the person's bones with
// the part bones first, so a skin bone r is Bones[SkinBoneStart + r - FLUID_SKIN_BONE_FIRST] of its
// FluidPersonRecord, and none at all unless r - FLUID_SKIN_BONE_FIRST < SkinBoneCount. Bone0 bits 16-23
// (FLUID_CONTACT_PART_SHIFT) carry the vertex's FluidBodyParts index.
// std430, 48 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidContactVertex
{
    public const int Size = 48;

    // m, bind pose
    public float3 Position;
    // octahedral
    public uint Normal;
    public float4 Weights;
    // bone in the low 16 bits, body part in bits 16-23
    public uint Bone0;
    public uint Bone1;
    public uint Bone2;
    public uint Bone3;
}
