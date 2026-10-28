// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// Every option splits a URL into its own MeshDataAsset instance; requests with equal descriptors share
// a mesh. MeshIndex -1 is the whole file concatenated, 0 and up only that mesh.
public sealed record MeshVariantDescriptor(
    float ImportScale,
    bool KeepReadable,
    int MeshIndex = -1,
    // Unwelded, with each triangle's corners in the vertex colours, for the wireframe material
    // (PhosBarycentrics); a separate variant, so the same file can be worn plain and as wires side by side.
    bool WireframeBarycentrics = false) : IAssetVariantDescriptor
{
    public static readonly MeshVariantDescriptor Default = new(ImportScale: 1.0f, KeepReadable: false);
}
