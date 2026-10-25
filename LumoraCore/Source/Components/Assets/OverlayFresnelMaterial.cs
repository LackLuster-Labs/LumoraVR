// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Fresnel outline drawn in the overlay band: additive, never depth-written, never depth-tested, both
// sides drawn, sitting in the same late render queue as the overlay unlit material so it stays
// visible through the scene. Ordering inside the band is the render queue's job.
//
// Front and Behind are a real occlusion answer, not a face-orientation split: with the depth test
// off the rasteriser cannot tell, so the shader reads the opaque pass's depth back and compares
// view-space distances. Surfaces that never wrote depth (other overlays, the sky) count as not
// occluding. -xlinka
[ComponentCategory("Assets/Materials")]
public class OverlayFresnelMaterial : MaterialProvider, ICommonMaterial
{
    // 1 is linear, higher pins the effect to the silhouette
    [Group("Fresnel")]
    [Range(0.1f, 16f, "0.00")]
    public readonly Sync<float> Exponent;

    [Group("Front")]
    public readonly Sync<colorHDR> FrontNearColor;

    [Group("Front")]
    public readonly Sync<colorHDR> FrontFarColor;

    [Group("Front")]
    public readonly Sync<float2> FrontTextureScale;

    [Group("Front")]
    public readonly Sync<float2> FrontTextureOffset;

    [Group("Behind")]
    public readonly Sync<colorHDR> BehindNearColor;

    [Group("Behind")]
    public readonly Sync<colorHDR> BehindFarColor;

    [Group("Behind")]
    public readonly Sync<float2> BehindTextureScale;

    [Group("Behind")]
    public readonly Sync<float2> BehindTextureOffset;

    // shared by both states; the front and behind UV sets pick different parts of it
    [Group("Rendering")]
    public readonly AssetRef<TextureAsset> Texture;

    [Group("Rendering")]
    public readonly Sync<bool> UseVertexColor;

    // metres of slack on the occlusion test, against depth-precision flicker
    [Group("Rendering")]
    [Range(0f, 0.5f, "0.000")]
    public readonly Sync<float> DepthBias;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    // Bends the front set's falloff after the exponent; the behind set keeps the plain one.
    [Group("Fresnel")]
    public readonly Sync<float> GammaCurve;

    // A colour's own texture under its own transform, in place of the shared Texture. Left empty, the colour
    // reads Texture under its set's transform, as every overlay fresnel did before these existed.
    [Group("Front")]
    public readonly AssetRef<TextureAsset> FrontNearTexture;
    [Group("Front")]
    public readonly Sync<float2> FrontNearTextureScale;
    [Group("Front")]
    public readonly Sync<float2> FrontNearTextureOffset;
    [Group("Front")]
    public readonly AssetRef<TextureAsset> FrontFarTexture;
    [Group("Front")]
    public readonly Sync<float2> FrontFarTextureScale;
    [Group("Front")]
    public readonly Sync<float2> FrontFarTextureOffset;
    [Group("Behind")]
    public readonly AssetRef<TextureAsset> BehindNearTexture;
    [Group("Behind")]
    public readonly Sync<float2> BehindNearTextureScale;
    [Group("Behind")]
    public readonly Sync<float2> BehindNearTextureOffset;
    [Group("Behind")]
    public readonly AssetRef<TextureAsset> BehindFarTexture;
    [Group("Behind")]
    public readonly Sync<float2> BehindFarTextureScale;
    [Group("Behind")]
    public readonly Sync<float2> BehindFarTextureOffset;

    [Group("Rendering")]
    public readonly AssetRef<TextureAsset> NormalMap;
    // Wraps every texture around the centre of the UV square, angle across U and radius along V. The power
    // bends the radius.
    [Group("Rendering")]
    public readonly Sync<bool> PolarUVmapping;
    [Group("Rendering")]
    public readonly Sync<float> PolarPower;

    // The defaults are the band's own state: additive, both sides, no depth written. Auto writes no depth in
    // any blend; Opaque and Cutout paint the colour at full strength over what is behind.
    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    [Group("Rendering")]
    public readonly Sync<Culling> Culling;
    [Group("Rendering")]
    public readonly Sync<ZWrite> ZWrite;
    // A depth bias for the in-front-or-behind test: negative pulls the surface toward the camera, so less of it
    // counts as behind.
    [Group("Rendering")]
    public readonly Sync<float> OffsetFactor;
    [Group("Rendering")]
    public readonly Sync<float> OffsetUnits;

    // Whether the last update put a generated shader on in place of the shipped file.
    private bool _generatedVariant;

    protected override MaterialType MaterialType => MaterialType.OverlayFresnel;

    public colorHDR Color
    {
        get => FrontFarColor.Value;
        set => FrontFarColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => Texture.Target;
        set => Texture.Target = value;
    }

    public OverlayFresnelMaterial()
    {
        Exponent = new Sync<float>(this, 3f);
        FrontNearColor = new Sync<colorHDR>(this, colorHDR.Black);
        FrontFarColor = new Sync<colorHDR>(this, colorHDR.White);
        FrontTextureScale = new Sync<float2>(this, float2.One);
        FrontTextureOffset = new Sync<float2>(this, float2.Zero);
        BehindNearColor = new Sync<colorHDR>(this, colorHDR.Black);
        BehindFarColor = new Sync<colorHDR>(this, new colorHDR(0.25f, 0.25f, 0.25f, 1f));
        BehindTextureScale = new Sync<float2>(this, float2.One);
        BehindTextureOffset = new Sync<float2>(this, float2.Zero);
        Texture = new AssetRef<TextureAsset>(this);
        UseVertexColor = new Sync<bool>(this, false);
        DepthBias = new Sync<float>(this, 0.005f);
        // Same late band the overlay unlit material defaults to, so the two stack predictably.
        RenderQueue = new Sync<int>(this, 4010);

        GammaCurve = new Sync<float>(this, 1f);
        FrontNearTexture = new AssetRef<TextureAsset>(this);
        FrontNearTextureScale = new Sync<float2>(this, float2.One);
        FrontNearTextureOffset = new Sync<float2>(this, float2.Zero);
        FrontFarTexture = new AssetRef<TextureAsset>(this);
        FrontFarTextureScale = new Sync<float2>(this, float2.One);
        FrontFarTextureOffset = new Sync<float2>(this, float2.Zero);
        BehindNearTexture = new AssetRef<TextureAsset>(this);
        BehindNearTextureScale = new Sync<float2>(this, float2.One);
        BehindNearTextureOffset = new Sync<float2>(this, float2.Zero);
        BehindFarTexture = new AssetRef<TextureAsset>(this);
        BehindFarTextureScale = new Sync<float2>(this, float2.One);
        BehindFarTextureOffset = new Sync<float2>(this, float2.Zero);
        NormalMap = new AssetRef<TextureAsset>(this);
        PolarUVmapping = new Sync<bool>(this, false);
        PolarPower = new Sync<float>(this, 1f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Additive);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        _generatedVariant = MaterialVariantSource.Apply(asset, VariantSource(BlendMode.Value, Culling.Value, ZWrite.Value),
            ShippedFile, _generatedVariant);

        asset.SetFloat("Exponent", Exponent.Value);
        asset.SetFloat("GammaCurve", GammaCurve.Value);

        asset.SetColor("FrontNearColor", FrontNearColor.Value);
        asset.SetColor("FrontFarColor", FrontFarColor.Value);
        asset.SetFloat2("FrontTextureScale", FrontTextureScale.Value);
        asset.SetFloat2("FrontTextureOffset", FrontTextureOffset.Value);

        asset.SetColor("BehindNearColor", BehindNearColor.Value);
        asset.SetColor("BehindFarColor", BehindFarColor.Value);
        asset.SetFloat2("BehindTextureScale", BehindTextureScale.Value);
        asset.SetFloat2("BehindTextureOffset", BehindTextureOffset.Value);

        asset.SetTexture("Texture", Texture.Asset);
        asset.SetBool("UseVertexColor", UseVertexColor.Value);
        asset.SetFloat("DepthBias", DepthBias.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);

        Layer(asset, "FrontNearTexture", FrontNearTexture, FrontNearTextureScale, FrontNearTextureOffset);
        Layer(asset, "FrontFarTexture", FrontFarTexture, FrontFarTextureScale, FrontFarTextureOffset);
        Layer(asset, "BehindNearTexture", BehindNearTexture, BehindNearTextureScale, BehindNearTextureOffset);
        Layer(asset, "BehindFarTexture", BehindFarTexture, BehindFarTextureScale, BehindFarTextureOffset);
        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetBool("PolarMapping", PolarUVmapping.Value);
        asset.SetFloat("PolarPower", PolarPower.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
    }

    private static void Layer(MaterialAsset asset, string name, AssetRef<TextureAsset> texture, Sync<float2> scale, Sync<float2> offset)
    {
        asset.SetTexture(name, texture.Asset);
        asset.SetBool("Use" + name, texture.Asset != null);
        asset.SetFloat2(name + "Scale", scale.Value);
        asset.SetFloat2(name + "Offset", offset.Value);
    }

    private const string ShippedFile = "res://Shaders/Mat_OverlayFresnel.gdshader";

    // Null in the shipped state. The depth test stays off in every variant: the front and behind split reads
    // the scene's depth itself, which only a pass after the opaque one can.
    private static string? VariantSource(BlendMode blend, Culling culling, ZWrite zWrite)
    {
        bool writesDepth = zWrite == Assets.ZWrite.On;
        if (blend == Assets.BlendMode.Additive && culling == Assets.Culling.None && !writesDepth)
            return null;
        bool opaque = !RenderModeText.IsBlended(blend);
        return "shader_type spatial;\nrender_mode unshaded, " + RenderModeText.Blend(blend) + ", "
            + (writesDepth ? "depth_draw_always" : "depth_draw_never") + ", depth_test_disabled, "
            + RenderModeText.Cull(culling) + ";\n"
            + (opaque ? "#define OVERLAY_OPAQUE\n" : string.Empty)
            + "#include \"res://Shaders/OverlayFresnel_Core.gdshaderinc\"\n";
    }
}
