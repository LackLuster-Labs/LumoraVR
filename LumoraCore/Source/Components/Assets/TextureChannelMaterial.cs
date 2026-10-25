// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

namespace Lumora.Core.Assets;

// One channel of a texture shown as grey, for checking what a packed map holds: TextureChannel 0 is red, 1 green,
// 2 blue, 3 alpha. Any other value shows the texture as it is. Opaque and unlit. -xlinka
[ComponentCategory("Assets/Materials")]
public class TextureChannelMaterial : MaterialProvider
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly Sync<float> TextureChannel;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public TextureChannelMaterial()
    {
        Texture = new AssetRef<TextureAsset>(this);
        TextureChannel = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetCustomShader("res://Shaders/Mat_TextureChannel.gdshader");
        asset.SetTexture("ChannelTexture", Texture.Asset);
        asset.SetFloat("TextureChannel", TextureChannel.Value);
    }
}
