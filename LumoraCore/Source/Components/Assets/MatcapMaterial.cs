// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// the whole lighting response is read out of one texture indexed by the view-space normal, so the
// highlight follows the camera and the surface costs nothing per light; scene lights don't touch it
[ComponentCategory("Assets/Materials")]
public class MatcapMaterial : MaterialProvider, ICommonMaterial
{
    // without it, the surface is flat tint
    [Group("Matcap")]
    public readonly AssetRef<TextureAsset> Matcap;

    [Group("Matcap")]
    public readonly Sync<colorHDR> TintColor;

    // Laid over the lookup the view-space normal makes into the matcap, 0..1 on both axes.
    [Group("Matcap")]
    public readonly Sync<float2> MatcapScale;

    [Group("Matcap")]
    public readonly Sync<float2> MatcapOffset;

    // multiplied over the matcap result, for a base albedo or a decal sheet
    [Group("Base")]
    public readonly AssetRef<TextureAsset> BaseTexture;

    [Group("Base")]
    public readonly Sync<float2> TextureScale;

    [Group("Base")]
    public readonly Sync<float2> TextureOffset;

    [Group("Normal")]
    public readonly AssetRef<TextureAsset> NormalMap;

    [Group("Normal")]
    [Range(0f, 4f, "0.00")]
    public readonly Sync<float> NormalScale;

    [Group("Rendering")]
    public readonly Sync<bool> AlphaClip;

    [Group("Rendering")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> AlphaCutoff;

    [Group("Rendering")]
    public readonly Sync<int> RenderQueue;

    // Alpha is the blend every matcap had before it had a choice, with a depth pre-pass. Opaque and Cutout keep
    // the surface in the opaque pass and throw alpha away (AlphaClip still cuts); additive and multiply write no
    // depth.
    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;

    // Whether the last update put a generated shader on in place of the shipped file.
    private bool _generatedVariant;

    protected override MaterialType MaterialType => MaterialType.Matcap;

    public colorHDR Color
    {
        get => TintColor.Value;
        set => TintColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => Matcap.Target;
        set => Matcap.Target = value;
    }

    public MatcapMaterial()
    {
        Matcap = new AssetRef<TextureAsset>(this);
        TintColor = new Sync<colorHDR>(this, colorHDR.White);
        MatcapScale = new Sync<float2>(this, float2.One);
        MatcapOffset = new Sync<float2>(this, float2.Zero);
        BaseTexture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);
        AlphaClip = new Sync<bool>(this, false);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        RenderQueue = new Sync<int>(this, -1);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        _generatedVariant = MaterialVariantSource.Apply(asset, VariantSource(BlendMode.Value),
            "res://Shaders/Mat_Matcap.gdshader", _generatedVariant);

        asset.SetTexture("MatcapTexture", Matcap.Asset);
        asset.SetBool("UseMatcapTexture", Matcap.Asset != null);
        asset.SetColor("TintColor", TintColor.Value);
        asset.SetFloat2("MatcapScale", MatcapScale.Value);
        asset.SetFloat2("MatcapOffset", MatcapOffset.Value);

        asset.SetTexture("BaseTexture", BaseTexture.Asset);
        asset.SetBool("UseBaseTexture", BaseTexture.Asset != null);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetBool("AlphaClip", AlphaClip.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
    }

    // Null for Alpha, the shipped state.
    private static string? VariantSource(BlendMode blend)
    {
        if (blend == Assets.BlendMode.Alpha)
            return null;
        bool blended = RenderModeText.IsBlended(blend);
        string depth = blend switch
        {
            Assets.BlendMode.Additive or Assets.BlendMode.Multiply => ", depth_draw_never",
            Assets.BlendMode.Transparent => ", depth_prepass_alpha",
            _ => string.Empty,
        };
        return "shader_type spatial;\nrender_mode unshaded, " + RenderModeText.Blend(blend) + depth + ", cull_back;\n"
            + (blended ? "#define TRANSPARENT_PASS\n" : string.Empty)
            + "#include \"res://Shaders/Matcap_Core.gdshaderinc\"\n";
    }
}
