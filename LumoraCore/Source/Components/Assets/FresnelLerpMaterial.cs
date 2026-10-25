// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Unlit crossfade between two complete fresnel sets. Each set has a near colour and texture (facing
// the viewer), a far colour and texture (at a grazing angle), and its own exponent; Lerp slides
// between the two sets, so one field animates a whole look change instead of eight.
//
// Near/far is the fresnel term, not distance: near is where the surface faces you, far is the
// silhouette. -xlinka
[ComponentCategory("Assets/Materials")]
public class FresnelLerpMaterial : MaterialProvider, ICommonMaterial
{
    [Group("Lerp")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Lerp;

    // read off the red channel and multiplied in on top of the scalar Lerp
    [Group("Lerp")]
    public readonly AssetRef<TextureAsset> LerpTexture;

    [Group("Lerp")]
    public readonly Sync<float2> LerpTextureScale;

    [Group("Lerp")]
    public readonly Sync<float2> LerpTextureOffset;

    [Group("Set 0")]
    public readonly Sync<colorHDR> NearColor0;

    [Group("Set 0")]
    public readonly Sync<colorHDR> FarColor0;

    [Group("Set 0")]
    public readonly AssetRef<TextureAsset> NearTexture0;

    [Group("Set 0")]
    public readonly AssetRef<TextureAsset> FarTexture0;

    [Group("Set 0")]
    [Range(0.1f, 16f, "0.00")]
    public readonly Sync<float> Exponent0;

    [Group("Set 1")]
    public readonly Sync<colorHDR> NearColor1;

    [Group("Set 1")]
    public readonly Sync<colorHDR> FarColor1;

    [Group("Set 1")]
    public readonly AssetRef<TextureAsset> NearTexture1;

    [Group("Set 1")]
    public readonly AssetRef<TextureAsset> FarTexture1;

    [Group("Set 1")]
    [Range(0.1f, 16f, "0.00")]
    public readonly Sync<float> Exponent1;

    [Group("Rendering")]
    public readonly Sync<float2> TextureScale;

    [Group("Rendering")]
    public readonly Sync<float2> TextureOffset;

    [Group("Rendering")]
    public readonly Sync<bool> UseVertexColor;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    // Reads the lerp texture wrapped around the centre of the UV square, angle across U and radius along V,
    // under its own scale and offset. The power bends the radius.
    [Group("Lerp")]
    public readonly Sync<bool> LerpTexturePolarUV;
    [Group("Lerp")]
    public readonly Sync<float> LerpTexturePolarPower;

    // Bends both sets' falloff after the exponent.
    [Group("Rendering")]
    public readonly Sync<float> GammaCurve;
    [Group("Set 0")]
    public readonly AssetRef<TextureAsset> NormalMap0;
    [Group("Set 1")]
    public readonly AssetRef<TextureAsset> NormalMap1;

    // The defaults are the shipped state: blended with a depth pre-pass, back faces culled. Opaque and Cutout
    // keep the surface in the opaque pass; Auto writes depth there, and through the pre-pass when blended
    // except for additive and multiply, which write none.
    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    [Group("Rendering")]
    public readonly Sync<Culling> Culling;
    [Group("Rendering")]
    public readonly Sync<ZWrite> ZWrite;
    // DEPTH BIAS: toward the camera (negative) or away from it, in depth only.
    [Group("Rendering")]
    public readonly Sync<float> OffsetFactor;
    [Group("Rendering")]
    public readonly Sync<float> OffsetUnits;

    // Whether the last update put a generated shader on in place of the shipped file.
    private bool _generatedVariant;

    protected override MaterialType MaterialType => MaterialType.FresnelLerp;

    public colorHDR Color
    {
        get => NearColor0.Value;
        set => NearColor0.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => NearTexture0.Target;
        set => NearTexture0.Target = value;
    }

    public FresnelLerpMaterial()
    {
        Lerp = new Sync<float>(this, 0f);
        LerpTexture = new AssetRef<TextureAsset>(this);
        LerpTextureScale = new Sync<float2>(this, float2.One);
        LerpTextureOffset = new Sync<float2>(this, float2.Zero);

        NearColor0 = new Sync<colorHDR>(this, colorHDR.White);
        FarColor0 = new Sync<colorHDR>(this, colorHDR.Black);
        NearTexture0 = new AssetRef<TextureAsset>(this);
        FarTexture0 = new AssetRef<TextureAsset>(this);
        Exponent0 = new Sync<float>(this, 1f);

        NearColor1 = new Sync<colorHDR>(this, new colorHDR(0.8f, 0.8f, 0.8f, 1f));
        FarColor1 = new Sync<colorHDR>(this, new colorHDR(0.2f, 0.2f, 0.2f, 1f));
        NearTexture1 = new AssetRef<TextureAsset>(this);
        FarTexture1 = new AssetRef<TextureAsset>(this);
        Exponent1 = new Sync<float>(this, 1f);

        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        UseVertexColor = new Sync<bool>(this, false);
        RenderQueue = new Sync<int>(this, -1);

        LerpTexturePolarUV = new Sync<bool>(this, false);
        LerpTexturePolarPower = new Sync<float>(this, 1f);
        GammaCurve = new Sync<float>(this, 1f);
        NormalMap0 = new AssetRef<TextureAsset>(this);
        NormalMap1 = new AssetRef<TextureAsset>(this);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        _generatedVariant = MaterialVariantSource.Apply(asset,
            VariantSource(BlendMode.Value, Culling.Value, ZWrite.Value, depthOffset), ShippedFile, _generatedVariant);

        asset.SetFloat("Lerp", Lerp.Value);
        asset.SetBool("LerpTexturePolar", LerpTexturePolarUV.Value);
        asset.SetFloat("LerpTexturePolarPower", LerpTexturePolarPower.Value);
        asset.SetFloat("GammaCurve", GammaCurve.Value);
        asset.SetTexture("NormalMap0", NormalMap0.Asset);
        asset.SetBool("UseNormalMap0", NormalMap0.Asset != null);
        asset.SetTexture("NormalMap1", NormalMap1.Asset);
        asset.SetBool("UseNormalMap1", NormalMap1.Asset != null);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
        asset.SetTexture("LerpTexture", LerpTexture.Asset);
        asset.SetBool("UseLerpTexture", LerpTexture.Asset != null);
        asset.SetFloat2("LerpTextureScale", LerpTextureScale.Value);
        asset.SetFloat2("LerpTextureOffset", LerpTextureOffset.Value);

        asset.SetColor("NearColor0", NearColor0.Value);
        asset.SetColor("FarColor0", FarColor0.Value);
        asset.SetTexture("NearTexture0", NearTexture0.Asset);
        asset.SetBool("UseNearTexture0", NearTexture0.Asset != null);
        asset.SetTexture("FarTexture0", FarTexture0.Asset);
        asset.SetBool("UseFarTexture0", FarTexture0.Asset != null);
        asset.SetFloat("Exponent0", Exponent0.Value);

        asset.SetColor("NearColor1", NearColor1.Value);
        asset.SetColor("FarColor1", FarColor1.Value);
        asset.SetTexture("NearTexture1", NearTexture1.Asset);
        asset.SetBool("UseNearTexture1", NearTexture1.Asset != null);
        asset.SetTexture("FarTexture1", FarTexture1.Asset);
        asset.SetBool("UseFarTexture1", FarTexture1.Asset != null);
        asset.SetFloat("Exponent1", Exponent1.Value);

        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);
        asset.SetBool("UseVertexColor", UseVertexColor.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
    }

    private const string ShippedFile = "res://Shaders/Mat_FresnelLerp.gdshader";

    // Null in the shipped state.
    private static string? VariantSource(BlendMode blend, Culling culling, ZWrite zWrite, bool depthOffset)
    {
        bool blended = RenderModeText.IsBlended(blend);
        string auto = blend is Assets.BlendMode.Additive or Assets.BlendMode.Multiply ? "depth_draw_never"
            : blended ? "depth_prepass_alpha" : string.Empty;
        string depth = zWrite switch
        {
            Assets.ZWrite.On => "depth_draw_always",
            Assets.ZWrite.Off => "depth_draw_never",
            _ => auto,
        };
        if (blend is Assets.BlendMode.Alpha && depth == "depth_prepass_alpha" && culling == Assets.Culling.Back && !depthOffset)
            return null;

        var sb = new System.Text.StringBuilder("shader_type spatial;\nrender_mode unshaded, ");
        sb.Append(RenderModeText.Blend(blend));
        if (depth.Length > 0)
            sb.Append(", ").Append(depth);
        sb.Append(", ").Append(RenderModeText.Cull(culling)).Append(";\n");
        if (blended)
            sb.Append("#define TRANSPARENT_PASS\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        sb.Append("#include \"res://Shaders/FresnelLerp_Core.gdshaderinc\"\n");
        return sb.ToString();
    }
}
