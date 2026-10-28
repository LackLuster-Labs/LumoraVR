// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// The load options that distinguish one shared TextureAsset instance from another
// for the same URL: resolution cap, mipmaps, wrap modes, compression intent, normal-map flag, and the
// colour profile the pixels are stored in, and the sampling it asks of the slots it is bound to.
// Requests with equal descriptors share a texture; differing ones get separate instances.
//
// MaxSize is what makes the quality setting work without a reload: it is part of the
// descriptor, so raising or lowering the cap produces a DIFFERENT descriptor, the provider
// re-requests, and the manager hands back (or loads) the instance for the new cap while the old
// one stays alive for anyone still on it. Nothing has to be torn down and rebuilt by hand. -xlinka
public sealed record TextureVariantDescriptor(
    bool GenerateMipmaps,
    TextureWrapMode WrapU,
    TextureWrapMode WrapV,
    bool IsNormalMap,
    int MaxSize = 0,
    TextureCompressionKind Compression = TextureCompressionKind.Block,
    TextureProfile Profile = TextureProfile.Slot,
    // How the material slots it is bound to filter it, and whether they wrap it by WrapU/WrapV; Slot and false
    // leave both to each slot.
    TextureFilter Filter = TextureFilter.Slot,
    bool UseWrapModes = false) : IAssetVariantDescriptor
{
    public static readonly TextureVariantDescriptor Default =
        new(GenerateMipmaps: true, TextureWrapMode.Repeat, TextureWrapMode.Repeat, IsNormalMap: false);

    public TextureVariantId ResolveVariant(int sourceWidth, int sourceHeight) =>
        TextureVariantId.Select(sourceWidth, sourceHeight, MaxSize, GenerateMipmaps, Compression);
}
