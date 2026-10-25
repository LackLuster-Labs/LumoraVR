// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// The load options go out as the variant descriptor, so providers with the same URL and the same options
// share one mesh.
[ComponentCategory("Assets")]
public class MeshProvider : StaticAssetProvider<MeshDataAsset>
{
    // Keeps the CPU copy after the GPU upload, for physics or edits at runtime.
    public readonly Sync<bool> KeepReadable;

    public readonly Sync<float> ImportScale;

    // -1 decodes the whole file as one mesh; 0 and up decodes only that mesh, so a multi-mesh model can be
    // one asset per mesh, each with its own material.
    public readonly Sync<int> MeshIndex;

    // Unwelds the mesh and writes each triangle's corners into its vertex colours, which is what the wireframe
    // material draws its lines from. It costs the mesh its vertex sharing and its own vertex colours.
    public readonly Sync<bool> WireframeBarycentrics;

    public MeshProvider()
    {
        KeepReadable = new Sync<bool>(this, false);
        ImportScale = new Sync<float>(this, 1.0f);
        MeshIndex = new Sync<int>(this, -1);
        WireframeBarycentrics = new Sync<bool>(this, false);
    }

    protected override IAssetVariantDescriptor? GetVariantDescriptor() =>
        new MeshVariantDescriptor(ImportScale.Value, KeepReadable.Value, MeshIndex.Value, WireframeBarycentrics.Value);
}
