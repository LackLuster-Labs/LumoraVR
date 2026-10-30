// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Fluids;

// How Godot lays out a surface made with ARRAY_FORMAT_VERTEX | NORMAL | COLOR | CUSTOM0 (RGBA half), no
// tangents, no compression and FlagUseStorageBuffer, which is the format every fluid surface uses. The surface
// kernels write these buffers directly, so the bytes are pinned here from the renderer's own offset rules:
//
//   vertex buffer     all positions first, float3 each (12 B, no padding; a kernel declares them float[]), then
//                     all normals, octahedral 2 x 16 bit UNORM (4 B). With normals and no tangents the renderer
//                     reads the next vertex's normal in .zw, so the buffer carries 4 B of padding at the end.
//   attribute buffer  per vertex: COLOR RGBA8 (4 B) at 0, then CUSTOM0 RGBA half (8 B) at 4; stride 12.
//   index buffer      16 bit while a surface has 65,536 vertices or fewer, else 32 bit. Storage writes are 32
//                     bit words, so 16 bit indices are always written as whole quads (6 indices, 3 words).
//
// The hook asserts each of these against MeshSurfaceGetFormatOffset and the stride queries when it makes a
// surface, so a renderer change shows as a failed assert, never as garbage geometry.
//
// The surface has to be made with RenderingServer.MeshAddSurface from a dictionary carrying exactly this format.
// MeshAddSurfaceFromArrays adds a tangent whenever normals come without one, and then each normal entry is 8 B
// (normal and tangent) and the vertex buffer is vertices x 20 with no padding (G1, 10-30). The 4 B entry stays on
// purpose: the fluid's own materials need no tangent, and it halves the normal writes. -xlinka
public static class FluidMeshLayout
{
    public const int PositionStride = 12;
    public const int NormalStride = 4;
    public const int VertexBufferPadding = 4;
    public const int AttributeStride = 12;
    public const int ColorOffset = 0;
    public const int Custom0Offset = 4;
    public const int QuadIndices = 6;
    public const int Index16VertexLimit = 65536;

    public static int NormalOffset(int vertexCount) => vertexCount * PositionStride;

    public static int VertexBufferBytes(int vertexCount) => vertexCount * (PositionStride + NormalStride) + VertexBufferPadding;

    public static int AttributeBufferBytes(int vertexCount) => vertexCount * AttributeStride;

    public static int IndexStride(int vertexCount) => vertexCount > 0 && vertexCount <= Index16VertexLimit ? 2 : 4;

    // Capacity in indices, rounded up to whole quads.
    public static int IndexBufferBytes(int vertexCount, int indexCapacity)
    {
        int quads = (indexCapacity + QuadIndices - 1) / QuadIndices;
        return quads * QuadIndices * IndexStride(vertexCount);
    }
}
