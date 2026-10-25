// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Cel/toon lit material: the diffuse response is quantised into flat bands (or read off a ramp),
// with a rim light and an optional outline.
//
// The outline is an inverted hull. Godot 4 shaders have no second pass, so the hull lives in its own
// shader that the material hook attaches as this material's next pass; OutlineWidth and OutlineColor
// are routed to it. Width 0 discards the hull entirely rather than leaving it z-fighting on the
// surface. -xlinka
[ComponentCategory("Assets/Materials")]
public class FlatToonMaterial : MaterialProvider, ICommonMaterial
{
    [Group("Albedo")]
    public readonly Sync<colorHDR> AlbedoColor;

    [Group("Albedo")]
    public readonly AssetRef<TextureAsset> AlbedoTexture;

    [Group("Albedo")]
    public readonly Sync<float2> TextureScale;

    [Group("Albedo")]
    public readonly Sync<float2> TextureOffset;

    // multiplies albedo where no light reaches the surface
    [Group("Shading")]
    public readonly Sync<colorHDR> ShadeColor;

    // 1 gives the hard two-tone look
    [Group("Shading")]
    [Range(1f, 8f, "0")]
    public readonly Sync<int> Steps;

    // wrapped N.L (half-lambert) units
    [Group("Shading")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> ShadeThreshold;

    // not pixels, so it doesn't change with distance
    [Group("Shading")]
    [Range(0.001f, 0.5f, "0.000")]
    public readonly Sync<float> ShadeSoftness;

    // replaces the band quantiser outright when set; wrapped N.L drives U
    [Group("Shading")]
    public readonly AssetRef<TextureAsset> RampTexture;

    [Group("Rim")]
    public readonly Sync<colorHDR> RimColor;

    [Group("Rim")]
    [Range(0.5f, 16f, "0.0")]
    public readonly Sync<float> RimPower;

    [Group("Outline")]
    public readonly Sync<colorHDR> OutlineColor;

    // metres at one metre from the camera; 0 removes the outline pass
    [Group("Outline")]
    [Range(0f, 0.1f, "0.000")]
    public readonly Sync<float> OutlineWidth;

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

    protected override MaterialType MaterialType => MaterialType.FlatToon;

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

    public FlatToonMaterial()
    {
        AlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        AlbedoTexture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        ShadeColor = new Sync<colorHDR>(this, new colorHDR(0.4f, 0.42f, 0.55f, 1f));
        Steps = new Sync<int>(this, 2);
        ShadeThreshold = new Sync<float>(this, 0.5f);
        ShadeSoftness = new Sync<float>(this, 0.03f);
        RampTexture = new AssetRef<TextureAsset>(this);
        RimColor = new Sync<colorHDR>(this, colorHDR.Black);
        RimPower = new Sync<float>(this, 4f);
        OutlineColor = new Sync<colorHDR>(this, colorHDR.Black);
        OutlineWidth = new Sync<float>(this, 0f);
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalScale = new Sync<float>(this, 1f);
        AlphaClip = new Sync<bool>(this, false);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetColor("AlbedoColor", AlbedoColor.Value);
        asset.SetTexture("AlbedoTexture", AlbedoTexture.Asset);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);

        asset.SetColor("ShadeColor", ShadeColor.Value);
        asset.SetInt("Steps", System.Math.Clamp(Steps.Value, 1, 8));
        asset.SetFloat("ShadeThreshold", ShadeThreshold.Value);
        asset.SetFloat("ShadeSoftness", ShadeSoftness.Value);
        asset.SetTexture("RampTexture", RampTexture.Asset);
        asset.SetBool("UseRampTexture", RampTexture.Asset != null);

        asset.SetColor("RimColor", RimColor.Value);
        asset.SetFloat("RimPower", RimPower.Value);

        asset.SetColor("OutlineColor", OutlineColor.Value);
        // Unlike the other two, Mat_FlatToonOutline multiplies by view depth out to a metre, so this
        // number is a screen-space factor up close and metres beyond, and 0.1 really is its ceiling. It was the only one not clamped at all,
        // which let an imported value walk straight past the shader's hint_range - a hint the editor
        // honours and code does not. -xlinka
        asset.SetFloat("OutlineWidth", System.Math.Clamp(OutlineWidth.Value, 0f, 0.1f));

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat("NormalScale", NormalScale.Value);

        asset.SetBool("AlphaClip", AlphaClip.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
    }
}
