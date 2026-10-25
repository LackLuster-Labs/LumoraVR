// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

[ComponentCategory("Assets/Materials")]
public class OverlayUnlitMaterial : MaterialProvider, ICommonMaterial
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly Sync<float2> FrontTextureScale;
    public readonly Sync<float2> FrontTextureOffset;
    public readonly Sync<float2> BehindTextureScale;
    public readonly Sync<float2> BehindTextureOffset;
    public readonly Sync<colorHDR> FrontTintColor;
    public readonly Sync<colorHDR> BehindTintColor;
    public readonly Sync<bool> UseVertexColor;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    public readonly Sync<int> RenderQueue;

    // The behind state's own texture, under the behind transform. Left empty, behind reads Texture, as every
    // overlay did before it had one.
    public readonly AssetRef<TextureAsset> BehindTexture;
    // Cutout drops what falls under it, before the vertex colour.
    public readonly Sync<float> AlphaCutoff;
    // Wraps both textures around the centre of the UV square, angle across U and radius along V, each under its
    // own scale and offset. The power bends the radius.
    public readonly Sync<bool> PolarUVmapping;
    public readonly Sync<float> PolarPower;
    // How vertex colours blend across a triangle: Linear as always, or sRGB for gradients that step evenly.
    public readonly Sync<ColorProfile> VertexColorInterpolationSpace;
    // A depth bias for the in-front-or-behind test of the blended overlay: negative pulls the surface toward the
    // camera, so less of it counts as behind. The additive overlay splits by face and takes none.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;

    protected override MaterialType MaterialType => MaterialType.OverlayUnlit;

    public colorHDR Color
    {
        get => FrontTintColor.Value;
        set => FrontTintColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => Texture.Target;
        set => Texture.Target = value;
    }

    public OverlayUnlitMaterial()
    {
        Texture = new AssetRef<TextureAsset>(this);
        FrontTextureScale = new Sync<float2>(this, float2.One);
        FrontTextureOffset = new Sync<float2>(this, float2.Zero);
        BehindTextureScale = new Sync<float2>(this, float2.One);
        BehindTextureOffset = new Sync<float2>(this, float2.Zero);
        FrontTintColor = new Sync<colorHDR>(this, colorHDR.White);
        BehindTintColor = new Sync<colorHDR>(this, new colorHDR(0.5f, 0.5f, 0.5f, 0.5f));
        UseVertexColor = new Sync<bool>(this, true);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Additive);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Off);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.Disabled);
        RenderQueue = new Sync<int>(this, 4010);

        BehindTexture = new AssetRef<TextureAsset>(this);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        PolarUVmapping = new Sync<bool>(this, false);
        PolarPower = new Sync<float>(this, 1f);
        VertexColorInterpolationSpace = new Sync<ColorProfile>(this, ColorProfile.Linear);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetTexture("Texture", Texture.Asset);
        asset.SetFloat2("FrontTextureScale", FrontTextureScale.Value);
        asset.SetFloat2("FrontTextureOffset", FrontTextureOffset.Value);
        asset.SetFloat2("BehindTextureScale", BehindTextureScale.Value);
        asset.SetFloat2("BehindTextureOffset", BehindTextureOffset.Value);
        asset.SetColor("FrontTintColor", FrontTintColor.Value);
        asset.SetColor("BehindTintColor", BehindTintColor.Value);
        asset.SetBool("UseVertexColor", UseVertexColor.Value);
        asset.SetInt("ZWrite", (int)ZWrite.Value);
        asset.SetInt("ZTest", (int)ZTest.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
        asset.SetTexture("BehindTexture", BehindTexture.Asset);
        asset.SetBool("UseBehindTexture", BehindTexture.Asset != null);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetBool("PolarMapping", PolarUVmapping.Value);
        asset.SetFloat("PolarPower", PolarPower.Value);
        asset.SetInt("VertexColorInterpolationSpace", (int)VertexColorInterpolationSpace.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
    }
}
