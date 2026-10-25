// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Four PBS layers painted across a surface by ColorMap's RGBA, terrain style, the four shares normalised to
// one. A PackedHeightMap (a height per layer in RGBA) lets the tallest layer win inside
// HeightTransitionRange, so stones poke through sand rather than blur into it. Normals come two layers to a
// texture (xy and zw), metallic and smoothness likewise (MetallicMap01: layer 0 in RG, layer 1 in BA) and scale
// the values; PackedEmissionMap is one emission mask per layer and takes over from the four EmissiveMaps.
// All four layers share TextureScale and TextureOffset. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_Splatmap : MaterialProvider, ICommonMaterial
{
    [Group("Splat")]
    public readonly AssetRef<TextureAsset> ColorMap;

    [Group("Splat")]
    public readonly Sync<float2> ColorMapScale;

    [Group("Splat")]
    public readonly Sync<float2> ColorMapOffset;

    [Group("Splat")]
    public readonly AssetRef<TextureAsset> PackedHeightMap;

    [Group("Splat")]
    [Range(0f, 1f, "0.000")]
    public readonly Sync<float> HeightTransitionRange;

    [Group("Surface")]
    public readonly Sync<PBSWorkflow> Workflow;

    [Group("Surface")]
    public readonly Sync<float2> TextureScale;

    [Group("Surface")]
    public readonly Sync<float2> TextureOffset;

    [Group("Surface")]
    public readonly AssetRef<TextureAsset> PackedNormalMap01;

    [Group("Surface")]
    public readonly AssetRef<TextureAsset> PackedNormalMap23;

    [Group("Surface")]
    public readonly AssetRef<TextureAsset> PackedEmissionMap;

    [Group("Metallic")]
    public readonly AssetRef<TextureAsset> MetallicMap01;

    [Group("Metallic")]
    public readonly AssetRef<TextureAsset> MetallicMap23;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> AlbedoColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> AlbedoTexture0;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> EmissiveColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> EmissiveMap0;

    [Group("Layer 0")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale0;

    [Group("Layer 0")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic0;

    [Group("Layer 0")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness0;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> SpecularColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> SpecularMap0;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> AlbedoColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> AlbedoTexture1;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> EmissiveColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> EmissiveMap1;

    [Group("Layer 1")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale1;

    [Group("Layer 1")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic1;

    [Group("Layer 1")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness1;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> SpecularColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> SpecularMap1;

    [Group("Layer 2")]
    public readonly Sync<colorHDR> AlbedoColor2;

    [Group("Layer 2")]
    public readonly AssetRef<TextureAsset> AlbedoTexture2;

    [Group("Layer 2")]
    public readonly Sync<colorHDR> EmissiveColor2;

    [Group("Layer 2")]
    public readonly AssetRef<TextureAsset> EmissiveMap2;

    [Group("Layer 2")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale2;

    [Group("Layer 2")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic2;

    [Group("Layer 2")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness2;

    [Group("Layer 2")]
    public readonly Sync<colorHDR> SpecularColor2;

    [Group("Layer 2")]
    public readonly AssetRef<TextureAsset> SpecularMap2;

    [Group("Layer 3")]
    public readonly Sync<colorHDR> AlbedoColor3;

    [Group("Layer 3")]
    public readonly AssetRef<TextureAsset> AlbedoTexture3;

    [Group("Layer 3")]
    public readonly Sync<colorHDR> EmissiveColor3;

    [Group("Layer 3")]
    public readonly AssetRef<TextureAsset> EmissiveMap3;

    [Group("Layer 3")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale3;

    [Group("Layer 3")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic3;

    [Group("Layer 3")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness3;

    [Group("Layer 3")]
    public readonly Sync<colorHDR> SpecularColor3;

    [Group("Layer 3")]
    public readonly AssetRef<TextureAsset> SpecularMap3;

    [Group("Rendering")]
    public readonly Sync<Culling> Culling;

    [Group("Rendering")]
    public readonly Sync<float> OffsetFactor;

    [Group("Rendering")]
    public readonly Sync<float> OffsetUnits;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    private PBSSurfaceVariant _variant;
    private string? _source;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public colorHDR Color
    {
        get => AlbedoColor0.Value;
        set => AlbedoColor0.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => AlbedoTexture0.Target;
        set => AlbedoTexture0.Target = value;
    }

    public PBS_Splatmap()
    {
        ColorMap = new AssetRef<TextureAsset>(this);
        ColorMapScale = new Sync<float2>(this, float2.One);
        ColorMapOffset = new Sync<float2>(this, float2.Zero);
        PackedHeightMap = new AssetRef<TextureAsset>(this);
        HeightTransitionRange = new Sync<float>(this, 0.05f);

        Workflow = new Sync<PBSWorkflow>(this, PBSWorkflow.Metallic);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        PackedNormalMap01 = new AssetRef<TextureAsset>(this);
        PackedNormalMap23 = new AssetRef<TextureAsset>(this);
        PackedEmissionMap = new AssetRef<TextureAsset>(this);
        MetallicMap01 = new AssetRef<TextureAsset>(this);
        MetallicMap23 = new AssetRef<TextureAsset>(this);

        var specular = new colorHDR(0.2f, 0.2f, 0.2f, 0.25f);
        AlbedoColor0 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture0 = new AssetRef<TextureAsset>(this);
        EmissiveColor0 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap0 = new AssetRef<TextureAsset>(this);
        NormalScale0 = new Sync<float>(this, 1f);
        Metallic0 = new Sync<float>(this, 0f);
        Smoothness0 = new Sync<float>(this, 0.25f);
        SpecularColor0 = new Sync<colorHDR>(this, specular);
        SpecularMap0 = new AssetRef<TextureAsset>(this);

        AlbedoColor1 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture1 = new AssetRef<TextureAsset>(this);
        EmissiveColor1 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap1 = new AssetRef<TextureAsset>(this);
        NormalScale1 = new Sync<float>(this, 1f);
        Metallic1 = new Sync<float>(this, 0f);
        Smoothness1 = new Sync<float>(this, 0.25f);
        SpecularColor1 = new Sync<colorHDR>(this, specular);
        SpecularMap1 = new AssetRef<TextureAsset>(this);

        AlbedoColor2 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture2 = new AssetRef<TextureAsset>(this);
        EmissiveColor2 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap2 = new AssetRef<TextureAsset>(this);
        NormalScale2 = new Sync<float>(this, 1f);
        Metallic2 = new Sync<float>(this, 0f);
        Smoothness2 = new Sync<float>(this, 0.25f);
        SpecularColor2 = new Sync<colorHDR>(this, specular);
        SpecularMap2 = new AssetRef<TextureAsset>(this);

        AlbedoColor3 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture3 = new AssetRef<TextureAsset>(this);
        EmissiveColor3 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap3 = new AssetRef<TextureAsset>(this);
        NormalScale3 = new Sync<float>(this, 1f);
        Metallic3 = new Sync<float>(this, 0f);
        Smoothness3 = new Sync<float>(this, 0.25f);
        SpecularColor3 = new Sync<colorHDR>(this, specular);
        SpecularMap3 = new AssetRef<TextureAsset>(this);

        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool specular = Workflow.Value == PBSWorkflow.Specular;
        var variant = new PBSSurfaceVariant("PBS_Splat_Core", specular, BlendMode.Opaque, ZWrite.Auto, ZTest.LessOrEqual,
            Culling.Value, OffsetFactor.Value != 0f || OffsetUnits.Value != 0f, PBSSurfaceFeatures.None);
        if (_source == null || variant != _variant)
        {
            _variant = variant;
            _source = PBSSurfaceSource.Build(variant);
        }
        asset.SetCustomShaderSource(_source);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        asset.SetTexture("ColorMap", ColorMap.Asset);
        asset.SetFloat2("ColorMapScale", ColorMapScale.Value);
        asset.SetFloat2("ColorMapOffset", ColorMapOffset.Value);
        asset.SetTexture("PackedHeightMap", PackedHeightMap.Asset);
        asset.SetFloat("HeightTransitionRange", HeightTransitionRange.Value);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetColor("AlbedoColor0", AlbedoColor0.Value);
        asset.SetColor("AlbedoColor1", AlbedoColor1.Value);
        asset.SetColor("AlbedoColor2", AlbedoColor2.Value);
        asset.SetColor("AlbedoColor3", AlbedoColor3.Value);
        asset.SetTexture("AlbedoTexture0", AlbedoTexture0.Asset);
        asset.SetTexture("AlbedoTexture1", AlbedoTexture1.Asset);
        asset.SetTexture("AlbedoTexture2", AlbedoTexture2.Asset);
        asset.SetTexture("AlbedoTexture3", AlbedoTexture3.Asset);

        asset.SetColor("EmissiveColor0", EmissiveColor0.Value);
        asset.SetColor("EmissiveColor1", EmissiveColor1.Value);
        asset.SetColor("EmissiveColor2", EmissiveColor2.Value);
        asset.SetColor("EmissiveColor3", EmissiveColor3.Value);
        asset.SetTexture("EmissiveMap0", EmissiveMap0.Asset);
        asset.SetTexture("EmissiveMap1", EmissiveMap1.Asset);
        asset.SetTexture("EmissiveMap2", EmissiveMap2.Asset);
        asset.SetTexture("EmissiveMap3", EmissiveMap3.Asset);
        asset.SetTexture("PackedEmissionMap", PackedEmissionMap.Asset);

        asset.SetTexture("PackedNormalMap01", PackedNormalMap01.Asset);
        asset.SetTexture("PackedNormalMap23", PackedNormalMap23.Asset);
        asset.SetFloat("NormalScale0", NormalScale0.Value);
        asset.SetFloat("NormalScale1", NormalScale1.Value);
        asset.SetFloat("NormalScale2", NormalScale2.Value);
        asset.SetFloat("NormalScale3", NormalScale3.Value);

        if (specular)
        {
            asset.SetColor("SpecularColor0", SpecularColor0.Value);
            asset.SetColor("SpecularColor1", SpecularColor1.Value);
            asset.SetColor("SpecularColor2", SpecularColor2.Value);
            asset.SetColor("SpecularColor3", SpecularColor3.Value);
            asset.SetTexture("SpecularMap0", SpecularMap0.Asset);
            asset.SetTexture("SpecularMap1", SpecularMap1.Asset);
            asset.SetTexture("SpecularMap2", SpecularMap2.Asset);
            asset.SetTexture("SpecularMap3", SpecularMap3.Asset);
        }
        else
        {
            // One vector per quantity, a layer to a lane.
            asset.SetFloat4("Metallic", new float4(Metallic0.Value, Metallic1.Value, Metallic2.Value, Metallic3.Value));
            asset.SetFloat4("Smoothness", new float4(Smoothness0.Value, Smoothness1.Value, Smoothness2.Value, Smoothness3.Value));
            asset.SetTexture("MetallicMap01", MetallicMap01.Asset);
            asset.SetTexture("MetallicMap23", MetallicMap23.Asset);
        }
    }
}
