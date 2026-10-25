// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// The one-layer PBS surface the two-sided, rim lit and intersect glow materials share: albedo, emission,
// normal, occlusion, either workflow, alpha handling, culling, depth write and depth bias. Each material adds
// its effect's inputs and names the features its variant needs; the shader is generated from those
// (PBSSurfaceSource) over PBS_Surface_Core.
//
// Both workflows sit on one material. Metallic reads Metallic, Smoothness and the MetallicMap (red metal,
// alpha smoothness); specular reads SpecularColor and the SpecularMap (rgb reflectance, alpha smoothness).
// A map replaces the value it stands for. Blending is premultiplied: a blended surface keeps its reflections
// at full strength while its diffuse fades, which is how glass and visors read. -xlinka
public abstract class PBS_SurfaceBase : MaterialProvider, ICommonMaterial
{
    [Group("Albedo")]
    public readonly Sync<colorHDR> AlbedoColor;

    [Group("Albedo")]
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    [Group("Albedo")]
    public readonly Sync<float2> TextureScale;

    [Group("Albedo")]
    public readonly Sync<float2> TextureOffset;

    [Group("Surface")]
    public readonly Sync<PBSWorkflow> Workflow;

    [Group("Surface")]
    public readonly AssetRef<TextureAsset> NormalMap;

    [Group("Surface")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale;

    // Red is read.
    [Group("Surface")]
    public readonly AssetRef<TextureAsset> OcclusionMap;

    [Group("Metallic")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Metallic;

    [Group("Metallic")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Smoothness;

    [Group("Metallic")]
    public readonly AssetRef<TextureAsset> MetallicMap;

    [Group("Specular")]
    public readonly Sync<colorHDR> SpecularColor;

    [Group("Specular")]
    public readonly AssetRef<TextureAsset> SpecularMap;

    [Group("Emission")]
    public readonly Sync<colorHDR> EmissiveColor;

    [Group("Emission")]
    public readonly AssetRef<TextureAsset> EmissiveMap;

    [Group("Rendering")]
    public readonly Sync<AlphaHandling> AlphaHandling;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaClip;

    [Group("Rendering")]
    public readonly Sync<Culling> Culling;

    // Auto writes depth when opaque and not when blended. On gives a blended surface a depth pass of its own
    // first, so its faces sort against each other.
    [Group("Rendering")]
    public readonly Sync<ZWrite> ZWrite;

    // Depth bias, negative toward the camera. Either one non-zero puts the variant on a biased depth.
    [Group("Rendering")]
    public readonly Sync<float> OffsetFactor;

    [Group("Rendering")]
    public readonly Sync<float> OffsetUnits;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    private PBSSurfaceVariant _variant;
    private string? _source;
    private string _nextPass = string.Empty;

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

    protected PBS_SurfaceBase(Culling culling, AlphaHandling alpha)
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);

        Workflow = new Sync<PBSWorkflow>(this, PBSWorkflow.Metallic);
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);
        OcclusionMap = new AssetRef<TextureAsset>(this);

        Metallic = new Sync<float>(this, 0f);
        Smoothness = new Sync<float>(this, 0.25f);
        MetallicMap = new AssetRef<TextureAsset>(this);

        SpecularColor = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 0.25f));
        SpecularMap = new AssetRef<TextureAsset>(this);

        EmissiveColor = new Sync<colorHDR>(this, colorHDR.Black);
        EmissiveMap = new AssetRef<TextureAsset>(this);

        AlphaHandling = new Sync<AlphaHandling>(this, alpha);
        AlphaClip = new Sync<float>(this, 0.5f);
        Culling = new Sync<Culling>(this, culling);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    // The effect this material lays over the surface.
    protected abstract PBSSurfaceFeatures Features { get; }

    // Drawn as two passes, back faces then front faces, when blended with both sides showing.
    protected virtual bool BlendsInTwoPasses => false;

    protected abstract void UpdateEffect(MaterialAsset asset);

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool specular = Workflow.Value == PBSWorkflow.Specular;
        var variant = new PBSSurfaceVariant("PBS_Surface_Core", specular, PBSSurfaceSource.BlendFor(AlphaHandling.Value),
            ZWrite.Value, ZTest.LessOrEqual, Culling.Value, OffsetFactor.Value != 0f || OffsetUnits.Value != 0f, Features);
        if (_source == null || variant != _variant)
        {
            _variant = variant;
            bool twoPass = BlendsInTwoPasses && PBSSurfaceSource.IsBlended(variant.Blend) && variant.Culling == Assets.Culling.None;
            _source = twoPass ? PBSSurfaceSource.Build(variant, Assets.Culling.Front) : PBSSurfaceSource.Build(variant);
            _nextPass = twoPass ? PBSSurfaceSource.Build(variant, Assets.Culling.Back) : string.Empty;
        }
        asset.SetCustomShaderSource(_source);
        asset.SetNextPassShaderSource(_nextPass);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetFloat("NormalScale", NormalScale.Value);
        asset.SetTexture("OcclusionMap", OcclusionMap.Asset);

        if (specular)
        {
            asset.SetColor("SpecularColor", SpecularColor.Value);
            asset.SetTexture("SpecularMap", SpecularMap.Asset);
        }
        else
        {
            asset.SetFloat("Metallic", Metallic.Value);
            asset.SetFloat("Smoothness", Smoothness.Value);
            asset.SetTexture("MetallicMap", MetallicMap.Asset);
        }

        asset.SetColor("EmissiveColor", EmissiveColor.Value);
        asset.SetTexture("EmissiveMap", EmissiveMap.Asset);

        asset.SetFloat("AlphaClip", AlphaClip.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        UpdateEffect(asset);
    }
}
