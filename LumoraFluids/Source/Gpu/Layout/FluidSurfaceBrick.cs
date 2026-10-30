// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Runtime.InteropServices;

namespace Lumora.Fluids;

// A live surface brick (8x8x8 cells): the slab chunks it holds in the mesh buffers, its vertices and quads, its
// entry in the brick hash table, this frame's field slot and the flags of its last extraction. Index slabs start on
// a whole quad, so 16 bit indices never share a word across bricks.
// std430, 64 B; FluidLayout.glsli declares the same bytes.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FluidSurfaceBrick
{
    public const int Size = 64;

    public int X;
    public int Y;
    public int Z;
    public uint Renderer;
    // slab chunks held
    public uint ChunkCount;
    public uint Vertices;
    // brick hash table entry
    public uint TableEntry;
    public uint Quads;
    public uint Flags;
    public uint Particles;
    public uint LastFrame;
    // this frame's field slot
    public uint FieldSlot;
    // m
    public float CellSize;
    // flags of the last extraction
    public uint ExtractFlags;
    public uint Reserved1;
    public uint Reserved2;

    // Old names, kept until every user has moved over; they add no bytes.
    [global::System.Obsolete("Renamed ChunkCount.")]
    public uint VertexStart { readonly get => ChunkCount; set => ChunkCount = value; }
    [global::System.Obsolete("Renamed Vertices.")]
    public uint VertexCount { readonly get => Vertices; set => Vertices = value; }
    [global::System.Obsolete("Renamed TableEntry.")]
    public uint IndexStart { readonly get => TableEntry; set => TableEntry = value; }
    [global::System.Obsolete("Renamed Quads.")]
    public uint IndexCount { readonly get => Quads; set => Quads = value; }
    [global::System.Obsolete("Renamed FieldSlot.")]
    public uint Next { readonly get => FieldSlot; set => FieldSlot = value; }
    [global::System.Obsolete("Renamed ExtractFlags.")]
    public uint Reserved0 { readonly get => ExtractFlags; set => ExtractFlags = value; }
}
