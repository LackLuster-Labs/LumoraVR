// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// A main texture and a mask texture for one renderer, over a material shared with others: one unlit material
// and a row of signs, each showing its own picture through its own cut-out. An empty slot here clears the
// material's own texture for this renderer rather than letting it through.
[ComponentCategory("Assets/Material Property Blocks")]
public class MainAndMaskTexturePropertyBlock : MaterialPropertyBlockProvider
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly AssetRef<TextureAsset> MaskTexture;

    public MainAndMaskTexturePropertyBlock()
    {
        Texture = new AssetRef<TextureAsset>(this);
        MaskTexture = new AssetRef<TextureAsset>(this);
    }

    protected override void UpdateBlock(MaterialPropertyBlockAsset asset)
    {
        asset.SetTexture("Texture", Texture.Asset);
        asset.SetTexture("MaskTexture", MaskTexture.Asset);
    }
}
