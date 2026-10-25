// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Two whole PBS surfaces mixed into one: layer 0 at Lerp 0, layer 1 at Lerp 1, every map of both mixed by
// the same amount. LerpTexture, when set, paints the amount across the surface from its red channel. With
// MultiValue the texture is scaled by Lerp and each layer's reflectance map by its values; without it the
// texture and the maps stand alone. Both sides are lit as the side they are. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_TwoLayer : MaterialProvider, ICommonMaterial
{
    [Group("Blend")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Lerp;

    [Group("Blend")]
    public readonly AssetRef<TextureAsset> LerpTexture;

    [Group("Blend")]
    public readonly Sync<float2> LerpTextureScale;

    [Group("Blend")]
    public readonly Sync<float2> LerpTextureOffset;

    [Group("Blend")]
    public readonly Sync<bool> MultiValue;

    [Group("Surface")]
    public readonly Sync<PBSWorkflow> Workflow;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> AlbedoColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> AlbedoTexture0;

    [Group("Layer 0")]
    public readonly Sync<float2> Texture0Scale;

    [Group("Layer 0")]
    public readonly Sync<float2> Texture0Offset;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> NormalMap0;

    [Group("Layer 0")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> OcclusionMap0;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> EmissiveColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> EmissiveMap0;

    [Group("Layer 0")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic0;

    [Group("Layer 0")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> MetallicMap0;

    [Group("Layer 0")]
    public readonly Sync<colorHDR> SpecularColor0;

    [Group("Layer 0")]
    public readonly AssetRef<TextureAsset> SpecularMap0;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> AlbedoColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> AlbedoTexture1;

    [Group("Layer 1")]
    public readonly Sync<float2> Texture1Scale;

    [Group("Layer 1")]
    public readonly Sync<float2> Texture1Offset;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> NormalMap1;

    [Group("Layer 1")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> OcclusionMap1;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> EmissiveColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> EmissiveMap1;

    [Group("Layer 1")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic1;

    [Group("Layer 1")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> MetallicMap1;

    [Group("Layer 1")]
    public readonly Sync<colorHDR> SpecularColor1;

    [Group("Layer 1")]
    public readonly AssetRef<TextureAsset> SpecularMap1;

    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaCutoff;

    [Group("Rendering")]
    public readonly Sync<ZWrite> ZWrite;

    [Group("Rendering")]
    public readonly Sync<ZTest> ZTest;

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

    public PBS_TwoLayer()
    {
        Lerp = new Sync<float>(this, 0f);
        LerpTexture = new AssetRef<TextureAsset>(this);
        LerpTextureScale = new Sync<float2>(this, float2.One);
        LerpTextureOffset = new Sync<float2>(this, float2.Zero);
        MultiValue = new Sync<bool>(this, true);
        Workflow = new Sync<PBSWorkflow>(this, PBSWorkflow.Metallic);

        AlbedoColor0 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture0 = new AssetRef<TextureAsset>(this);
        Texture0Scale = new Sync<float2>(this, float2.One);
        Texture0Offset = new Sync<float2>(this, float2.Zero);
        NormalMap0 = new AssetRef<TextureAsset>(this);
        NormalScale0 = new Sync<float>(this, 1f);
        OcclusionMap0 = new AssetRef<TextureAsset>(this);
        EmissiveColor0 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap0 = new AssetRef<TextureAsset>(this);
        Metallic0 = new Sync<float>(this, 0f);
        Smoothness0 = new Sync<float>(this, 0.25f);
        MetallicMap0 = new AssetRef<TextureAsset>(this);
        SpecularColor0 = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
        SpecularMap0 = new AssetRef<TextureAsset>(this);

        AlbedoColor1 = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture1 = new AssetRef<TextureAsset>(this);
        Texture1Scale = new Sync<float2>(this, float2.One);
        Texture1Offset = new Sync<float2>(this, float2.Zero);
        NormalMap1 = new AssetRef<TextureAsset>(this);
        NormalScale1 = new Sync<float>(this, 1f);
        OcclusionMap1 = new AssetRef<TextureAsset>(this);
        EmissiveColor1 = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap1 = new AssetRef<TextureAsset>(this);
        Metallic1 = new Sync<float>(this, 0f);
        Smoothness1 = new Sync<float>(this, 0.25f);
        MetallicMap1 = new AssetRef<TextureAsset>(this);
        SpecularColor1 = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
        SpecularMap1 = new AssetRef<TextureAsset>(this);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool specular = Workflow.Value == PBSWorkflow.Specular;
        var variant = new PBSSurfaceVariant("PBS_TwoLayer_Core", specular, BlendMode.Value, ZWrite.Value, ZTest.Value,
            Culling.Value, OffsetFactor.Value != 0f || OffsetUnits.Value != 0f, PBSSurfaceFeatures.None);
        if (_source == null || variant != _variant)
        {
            _variant = variant;
            _source = PBSSurfaceSource.Build(variant);
        }
        asset.SetCustomShaderSource(_source);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("AlphaClip", AlphaCutoff.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        // "lerp" alone reads as the operation in a shader, so the amount goes by its job.
        asset.SetFloat("LerpAmount", Lerp.Value);
        asset.SetTexture("LerpTexture", LerpTexture.Asset);
        asset.SetFloat2("LerpTextureScale", LerpTextureScale.Value);
        asset.SetFloat2("LerpTextureOffset", LerpTextureOffset.Value);
        asset.SetBool("MultiplyValues", MultiValue.Value);

        asset.SetColor("AlbedoColor0", AlbedoColor0.Value);
        asset.SetTexture("AlbedoTexture0", AlbedoTexture0.Asset);
        asset.SetFloat2("Texture0Scale", Texture0Scale.Value);
        asset.SetFloat2("Texture0Offset", Texture0Offset.Value);
        asset.SetTexture("NormalMap0", NormalMap0.Asset);
        asset.SetFloat("NormalScale0", NormalScale0.Value);
        asset.SetTexture("OcclusionMap0", OcclusionMap0.Asset);
        asset.SetColor("EmissiveColor0", EmissiveColor0.Value);
        asset.SetTexture("EmissiveMap0", EmissiveMap0.Asset);

        asset.SetColor("AlbedoColor1", AlbedoColor1.Value);
        asset.SetTexture("AlbedoTexture1", AlbedoTexture1.Asset);
        asset.SetFloat2("Texture1Scale", Texture1Scale.Value);
        asset.SetFloat2("Texture1Offset", Texture1Offset.Value);
        asset.SetTexture("NormalMap1", NormalMap1.Asset);
        asset.SetFloat("NormalScale1", NormalScale1.Value);
        asset.SetTexture("OcclusionMap1", OcclusionMap1.Asset);
        asset.SetColor("EmissiveColor1", EmissiveColor1.Value);
        asset.SetTexture("EmissiveMap1", EmissiveMap1.Asset);

        if (specular)
        {
            asset.SetColor("SpecularColor0", SpecularColor0.Value);
            asset.SetTexture("SpecularMap0", SpecularMap0.Asset);
            asset.SetColor("SpecularColor1", SpecularColor1.Value);
            asset.SetTexture("SpecularMap1", SpecularMap1.Asset);
        }
        else
        {
            asset.SetFloat("Metallic0", Metallic0.Value);
            asset.SetFloat("Smoothness0", Smoothness0.Value);
            asset.SetTexture("MetallicMap0", MetallicMap0.Asset);
            asset.SetFloat("Metallic1", Metallic1.Value);
            asset.SetFloat("Smoothness1", Smoothness1.Value);
            asset.SetTexture("MetallicMap1", MetallicMap1.Asset);
        }
    }
}
