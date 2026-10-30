// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// One person near fluid: where their bones, proxies, contact mesh and residue bones sit in the frame's arrays.
// Flags pack the stick mode, the view LOD and the local, view-only, frozen and drop-anchor bits (FluidLayout). The
// bones are two ranges: the frame range (the part bones in FluidBodyParts order, then the proxy frames) and the
// skin range the contact vertices are weighted to, both filled at every bone upload.
// std430, 80 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidPersonRecord
{
    public const int Size = 80;

    // frame range: part bones, then proxy frames
    public uint BoneStart;
    public uint BoneCount;
    public uint ProxyStart;
    public uint ProxyCount;
    public uint VertexStart;
    public uint VertexCount;
    public uint TriangleStart;
    public uint TriangleCount;
    public uint BlendStart;
    public uint BlendCount;
    public uint Flags;
    // allocation id
    public uint User;
    // avatar factor
    public float Stickiness;
    // m
    public float SoleInset;
    public uint ResidueBoneStart;
    public uint ResidueBoneCount;
    // skin range: contact vertex bone r is Bones[SkinBoneStart + r - FLUID_SKIN_BONE_FIRST]
    public uint SkinBoneStart;
    public uint SkinBoneCount;
    // x the substance's, on this person's skin (FluidSkinCollide)
    public float SkinFriction;
    // x the substance's adhesion, on this person's skin
    public float SkinWetting;
}
