// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// PBS_ColorMaskMetallic in the specular workflow: the same four tint slots and four emission slots picked by
// the mask's channels, over a surface whose reflectance is a colour. SpecularColor's RGB is the reflectance and
// its alpha the smoothness; SpecularMap carries the same pair and replaces the colour when set. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_ColorMaskSpecular : MaterialProvider, ICommonMaterial
{
    public readonly Sync<colorHDR> AlbedoColor0;
    public readonly Sync<colorHDR> AlbedoColor1;
    public readonly Sync<colorHDR> AlbedoColor2;
    public readonly Sync<colorHDR> AlbedoColor3;

    public readonly AssetRef<TextureAsset> AlbedoTexture;

    // Which of the four colours each pixel takes, one per channel, at its own tiling.
    public readonly AssetRef<TextureAsset> ColorMask;

    public readonly Sync<float2> TextureScale;
    public readonly Sync<float2> TextureOffset;
    public readonly Sync<float2> ColorMaskScale;
    public readonly Sync<float2> ColorMaskOffset;

    public readonly Sync<colorHDR> SpecularColor;
    public readonly AssetRef<TextureAsset> SpecularMap;

    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float> NormalScale;

    // Red is read.
    public readonly AssetRef<TextureAsset> OcclusionMap;

    public readonly Sync<colorHDR> EmissiveColor0;
    public readonly Sync<colorHDR> EmissiveColor1;
    public readonly Sync<colorHDR> EmissiveColor2;
    public readonly Sync<colorHDR> EmissiveColor3;

    public readonly AssetRef<TextureAsset> EmissiveMap;

    public readonly Sync<bool> Transparent;
    public readonly Sync<bool> ForceZWrite;

    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;

    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public colorHDR Color
    {
        get => AlbedoColor0.Value;
        set => AlbedoColor0.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => AlbedoTexture.Target;
        set => AlbedoTexture.Target = value;
    }

    public PBS_ColorMaskSpecular()
    {
        AlbedoColor0 = new Sync<colorHDR>(this, new colorHDR(1f, 0f, 0f, 1f));
        AlbedoColor1 = new Sync<colorHDR>(this, new colorHDR(0f, 1f, 0f, 1f));
        AlbedoColor2 = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 1f, 1f));
        AlbedoColor3 = new Sync<colorHDR>(this, colorHDR.White);

        AlbedoTexture = new AssetRef<TextureAsset>(this);
        ColorMask = new AssetRef<TextureAsset>(this);

        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        ColorMaskScale = new Sync<float2>(this, float2.One);
        ColorMaskOffset = new Sync<float2>(this, float2.Zero);

        SpecularColor = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
        SpecularMap = new AssetRef<TextureAsset>(this);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);

        OcclusionMap = new AssetRef<TextureAsset>(this);

        EmissiveColor0 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveColor1 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveColor2 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveColor3 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap = new AssetRef<TextureAsset>(this);

        Transparent = new Sync<bool>(this, false);
        ForceZWrite = new Sync<bool>(this, false);

        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetCustomShader(Transparent.Value
            ? "res://Shaders/Mat_PBS_ColorMaskSpecularAlpha.gdshader"
            : "res://Shaders/Mat_PBS_ColorMaskSpecular.gdshader");
        asset.SetBlendMode(Transparent.Value ? BlendMode.Transparent : BlendMode.Opaque);

        asset.SetColor("AlbedoColor0", AlbedoColor0.Value);
        asset.SetColor("AlbedoColor1", AlbedoColor1.Value);
        asset.SetColor("AlbedoColor2", AlbedoColor2.Value);
        asset.SetColor("AlbedoColor3", AlbedoColor3.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);
        asset.SetTexture("ColorMask", ColorMask.Asset);

        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);
        asset.SetFloat2("ColorMaskScale", ColorMaskScale.Value);
        asset.SetFloat2("ColorMaskOffset", ColorMaskOffset.Value);

        asset.SetColor("SpecularColor", SpecularColor.Value);
        asset.SetTexture("SpecularMap", SpecularMap.Asset);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetFloat("NormalScale", NormalScale.Value);
        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);

        asset.SetColor("EmissiveColor0", EmissiveColor0.Value);
        asset.SetColor("EmissiveColor1", EmissiveColor1.Value);
        asset.SetColor("EmissiveColor2", EmissiveColor2.Value);
        asset.SetColor("EmissiveColor3", EmissiveColor3.Value);
        asset.SetTexture("EmissiveMap", EmissiveMap.Asset);

        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
    }
}
