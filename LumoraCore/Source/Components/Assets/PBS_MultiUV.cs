// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// PBS where every map picks its own UV set and tiling: a tiling albedo on the first set with a baked
// occlusion or glow on the second, say. The secondary albedo multiplies the first; the secondary emission map
// adds its own tinted glow on top of the first. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_MultiUV : MaterialProvider, ICommonMaterial
{
    [Group("Albedo")]
    public readonly Sync<colorHDR> AlbedoColor;

    [Group("Albedo")]
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    [Group("Albedo")]
    public readonly Sync<float2> AlbedoScale;

    [Group("Albedo")]
    public readonly Sync<float2> AlbedoOffset;

    [Group("Albedo")]
    public readonly Sync<TextureUVChannel> AlbedoUV;

    [Group("Albedo")]
    public readonly AssetRef<TextureAsset> SecondaryAlbedoTexture;

    [Group("Albedo")]
    public readonly Sync<float2> SecondaryAlbedoScale;

    [Group("Albedo")]
    public readonly Sync<float2> SecondaryAlbedoOffset;

    [Group("Albedo")]
    public readonly Sync<TextureUVChannel> SecondaryAlbedoUV;

    [Group("Surface")]
    public readonly Sync<PBSWorkflow> Workflow;

    [Group("Surface")]
    public readonly AssetRef<TextureAsset> NormalMap;

    [Group("Surface")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale;

    [Group("Surface")]
    public readonly Sync<float2> NormalMapScale;

    [Group("Surface")]
    public readonly Sync<float2> NormalMapOffset;

    [Group("Surface")]
    public readonly Sync<TextureUVChannel> NormalMapUV;

    // Red is read.
    [Group("Surface")]
    public readonly AssetRef<TextureAsset> OcclusionMap;

    [Group("Surface")]
    public readonly Sync<float2> OcclusionMapScale;

    [Group("Surface")]
    public readonly Sync<float2> OcclusionMapOffset;

    [Group("Surface")]
    public readonly Sync<TextureUVChannel> OcclusionMapUV;

    [Group("Metallic")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic;

    [Group("Metallic")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness;

    [Group("Metallic")]
    public readonly AssetRef<TextureAsset> MetallicMap;

    [Group("Metallic")]
    public readonly Sync<float2> MetallicMapScale;

    [Group("Metallic")]
    public readonly Sync<float2> MetallicMapOffset;

    [Group("Metallic")]
    public readonly Sync<TextureUVChannel> MetallicMapUV;

    [Group("Specular")]
    public readonly Sync<colorHDR> SpecularColor;

    [Group("Specular")]
    public readonly AssetRef<TextureAsset> SpecularMap;

    [Group("Specular")]
    public readonly Sync<float2> SpecularMapScale;

    [Group("Specular")]
    public readonly Sync<float2> SpecularMapOffset;

    [Group("Specular")]
    public readonly Sync<TextureUVChannel> SpecularMapUV;

    [Group("Emission")]
    public readonly Sync<colorHDR> EmissiveColor;

    [Group("Emission")]
    public readonly AssetRef<TextureAsset> EmissiveMap;

    [Group("Emission")]
    public readonly Sync<float2> EmissionMapScale;

    [Group("Emission")]
    public readonly Sync<float2> EmissionMapOffset;

    [Group("Emission")]
    public readonly Sync<TextureUVChannel> EmissionMapUV;

    [Group("Emission")]
    public readonly Sync<colorHDR> SecondaryEmissiveColor;

    [Group("Emission")]
    public readonly AssetRef<TextureAsset> SecondaryEmissiveMap;

    [Group("Emission")]
    public readonly Sync<float2> SecondaryEmissionMapScale;

    [Group("Emission")]
    public readonly Sync<float2> SecondaryEmissionMapOffset;

    [Group("Emission")]
    public readonly Sync<TextureUVChannel> SecondaryEmissionMapUV;

    [Group("Rendering")]
    public readonly Sync<AlphaHandling> AlphaHandling;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaClip;

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
        get => AlbedoColor.Value;
        set => AlbedoColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => AlbedoTexture.Target;
        set => AlbedoTexture.Target = value;
    }

    public PBS_MultiUV()
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        AlbedoScale = new Sync<float2>(this, float2.One);
        AlbedoOffset = new Sync<float2>(this, float2.Zero);
        AlbedoUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        SecondaryAlbedoTexture = new AssetRef<TextureAsset>(this);
        SecondaryAlbedoScale = new Sync<float2>(this, float2.One);
        SecondaryAlbedoOffset = new Sync<float2>(this, float2.Zero);
        SecondaryAlbedoUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        Workflow = new Sync<PBSWorkflow>(this, PBSWorkflow.Metallic);
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);
        NormalMapScale = new Sync<float2>(this, float2.One);
        NormalMapOffset = new Sync<float2>(this, float2.Zero);
        NormalMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        OcclusionMap = new AssetRef<TextureAsset>(this);
        OcclusionMapScale = new Sync<float2>(this, float2.One);
        OcclusionMapOffset = new Sync<float2>(this, float2.Zero);
        OcclusionMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        Metallic = new Sync<float>(this, 0f);
        Smoothness = new Sync<float>(this, 0.25f);
        MetallicMap = new AssetRef<TextureAsset>(this);
        MetallicMapScale = new Sync<float2>(this, float2.One);
        MetallicMapOffset = new Sync<float2>(this, float2.Zero);
        MetallicMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        SpecularColor = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
        SpecularMap = new AssetRef<TextureAsset>(this);
        SpecularMapScale = new Sync<float2>(this, float2.One);
        SpecularMapOffset = new Sync<float2>(this, float2.Zero);
        SpecularMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        EmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap = new AssetRef<TextureAsset>(this);
        EmissionMapScale = new Sync<float2>(this, float2.One);
        EmissionMapOffset = new Sync<float2>(this, float2.Zero);
        EmissionMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);
        SecondaryEmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        SecondaryEmissiveMap = new AssetRef<TextureAsset>(this);
        SecondaryEmissionMapScale = new Sync<float2>(this, float2.One);
        SecondaryEmissionMapOffset = new Sync<float2>(this, float2.Zero);
        SecondaryEmissionMapUV = new Sync<TextureUVChannel>(this, TextureUVChannel.UV1);

        AlphaHandling = new Sync<AlphaHandling>(this, Assets.AlphaHandling.Opaque);
        AlphaClip = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool specular = Workflow.Value == PBSWorkflow.Specular;
        var variant = new PBSSurfaceVariant("PBS_MultiUV_Core", specular, PBSSurfaceSource.BlendFor(AlphaHandling.Value),
            Assets.ZWrite.Auto, Assets.ZTest.LessOrEqual, Culling.Value, OffsetFactor.Value != 0f || OffsetUnits.Value != 0f,
            PBSSurfaceFeatures.None);
        if (_source == null || variant != _variant)
        {
            _variant = variant;
            _source = PBSSurfaceSource.Build(variant);
        }
        asset.SetCustomShaderSource(_source);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("AlphaClip", AlphaClip.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        // The UV set names go in already lowercase: a capital in "UV" would split into u_v on the way to the
        // uniform.
        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);
        asset.SetFloat2("AlbedoScale", AlbedoScale.Value);
        asset.SetFloat2("AlbedoOffset", AlbedoOffset.Value);
        asset.SetInt("albedo_uv", (int)AlbedoUV.Value);
        asset.SetTexture("SecondaryAlbedoTexture", SecondaryAlbedoTexture.Asset);
        asset.SetFloat2("SecondaryAlbedoScale", SecondaryAlbedoScale.Value);
        asset.SetFloat2("SecondaryAlbedoOffset", SecondaryAlbedoOffset.Value);
        asset.SetInt("secondary_albedo_uv", (int)SecondaryAlbedoUV.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetFloat("NormalScale", NormalScale.Value);
        asset.SetFloat2("NormalMapScale", NormalMapScale.Value);
        asset.SetFloat2("NormalMapOffset", NormalMapOffset.Value);
        asset.SetInt("normal_map_uv", (int)NormalMapUV.Value);
        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);
        asset.SetFloat2("OcclusionMapScale", OcclusionMapScale.Value);
        asset.SetFloat2("OcclusionMapOffset", OcclusionMapOffset.Value);
        asset.SetInt("occlusion_map_uv", (int)OcclusionMapUV.Value);

        if (specular)
        {
            asset.SetColor("SpecularColor", SpecularColor.Value);
            asset.SetTexture("SpecularMap", SpecularMap.Asset);
            asset.SetFloat2("SpecularMapScale", SpecularMapScale.Value);
            asset.SetFloat2("SpecularMapOffset", SpecularMapOffset.Value);
            asset.SetInt("specular_map_uv", (int)SpecularMapUV.Value);
        }
        else
        {
            asset.SetFloat("Metallic", Metallic.Value);
            asset.SetFloat("Smoothness", Smoothness.Value);
            asset.SetTexture("MetallicMap", MetallicMap.Asset);
            asset.SetFloat2("MetallicMapScale", MetallicMapScale.Value);
            asset.SetFloat2("MetallicMapOffset", MetallicMapOffset.Value);
            asset.SetInt("metallic_map_uv", (int)MetallicMapUV.Value);
        }

        asset.SetColor("EmissiveColor", EmissiveColor.Value);
        asset.SetTexture("EmissiveMap", EmissiveMap.Asset);
        asset.SetFloat2("EmissionMapScale", EmissionMapScale.Value);
        asset.SetFloat2("EmissionMapOffset", EmissionMapOffset.Value);
        asset.SetInt("emission_map_uv", (int)EmissionMapUV.Value);
        asset.SetColor("SecondaryEmissiveColor", SecondaryEmissiveColor.Value);
        asset.SetTexture("SecondaryEmissiveMap", SecondaryEmissiveMap.Asset);
        asset.SetFloat2("SecondaryEmissionMapScale", SecondaryEmissionMapScale.Value);
        asset.SetFloat2("SecondaryEmissionMapOffset", SecondaryEmissionMapOffset.Value);
        asset.SetInt("secondary_emission_map_uv", (int)SecondaryEmissionMapUV.Value);
    }
}
