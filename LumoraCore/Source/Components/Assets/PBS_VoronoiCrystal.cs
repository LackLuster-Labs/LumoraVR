// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Lit crystal cells: the surface's UV square cut into NoiseScale Voronoi cells that tile across its edges, each
// a facet with a colour, emission and smoothness of its own, and a seam EdgeThickness wide where cells meet,
// with its own colour, emission, smoothness, metallic and a lean of the normal across it. Each cell picks its
// colour and emission from ColorGradient and EmissionGradient, its smoothness from SmoothnessGradient (unset
// gradients read as white), and tilts the normal map its own way. Animating NoiseAnimationOffset makes the cells
// crawl. Opaque, metallic workflow. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_VoronoiCrystal : MaterialProvider
{
    [Group("Cells")]
    public readonly Sync<float2> NoiseScale;
    public readonly Sync<float> NoiseAnimationOffset;
    public readonly Sync<colorHDR> CellColorTint;
    public readonly AssetRef<TextureAsset> ColorGradient;
    public readonly Sync<colorHDR> EmissionColor;
    public readonly AssetRef<TextureAsset> EmissionGradient;
    public readonly AssetRef<TextureAsset> SmoothnessGradient;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> CellSmoothness;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> CellMetallic;

    [Group("Normal")]
    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float> NormalStrength;
    public readonly Sync<float2> NormalTextureScale;
    public readonly Sync<float2> NormalTextureOffset;

    [Group("Edges")]
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> EdgeThickness;
    public readonly Sync<colorHDR> EdgeColor;
    public readonly Sync<colorHDR> EdgeEmission;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> EdgeSmoothness;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> EdgeMetallic;
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> EdgeNormalStrength;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public PBS_VoronoiCrystal()
    {
        NoiseScale = new Sync<float2>(this, float2.One * 10f);
        NoiseAnimationOffset = new Sync<float>(this, 0f);
        CellColorTint = new Sync<colorHDR>(this, colorHDR.White);
        ColorGradient = new AssetRef<TextureAsset>(this);
        EmissionColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        EmissionGradient = new AssetRef<TextureAsset>(this);
        SmoothnessGradient = new AssetRef<TextureAsset>(this);
        CellSmoothness = new Sync<float>(this, 0.5f);
        CellMetallic = new Sync<float>(this, 0.1f);

        NormalMap = new AssetRef<TextureAsset>(this);
        NormalStrength = new Sync<float>(this, 1f);
        NormalTextureScale = new Sync<float2>(this, float2.One);
        NormalTextureOffset = new Sync<float2>(this, float2.Zero);

        EdgeThickness = new Sync<float>(this, 0.1f);
        EdgeColor = new Sync<colorHDR>(this, colorHDR.Black);
        EdgeEmission = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        EdgeSmoothness = new Sync<float>(this, 0.8f);
        EdgeMetallic = new Sync<float>(this, 0.1f);
        EdgeNormalStrength = new Sync<float>(this, 0.5f);
    }

    public override bool TryGetPrimaryColor(out colorHDR color)
    {
        color = CellColorTint.Value;
        return true;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetCustomShader("res://Shaders/Mat_PBS_VoronoiCrystal.gdshader");

        asset.SetFloat2("NoiseScale", NoiseScale.Value);
        asset.SetFloat("NoiseAnimationOffset", NoiseAnimationOffset.Value);
        asset.SetColor("CellColorTint", CellColorTint.Value);
        asset.SetTexture("ColorGradientTexture", ColorGradient.Asset);
        asset.SetColor("EmissionColor", EmissionColor.Value);
        asset.SetTexture("EmissionGradientTexture", EmissionGradient.Asset);
        asset.SetTexture("SmoothnessGradientTexture", SmoothnessGradient.Asset);
        asset.SetFloat("CellSmoothness", CellSmoothness.Value);
        asset.SetFloat("CellMetallic", CellMetallic.Value);

        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetFloat("NormalStrength", NormalStrength.Value);
        asset.SetFloat2("NormalTextureScale", NormalTextureScale.Value);
        asset.SetFloat2("NormalTextureOffset", NormalTextureOffset.Value);

        asset.SetFloat("EdgeThickness", EdgeThickness.Value);
        asset.SetColor("EdgeColor", EdgeColor.Value);
        asset.SetColor("EdgeEmission", EdgeEmission.Value);
        asset.SetFloat("EdgeSmoothness", EdgeSmoothness.Value);
        asset.SetFloat("EdgeMetallic", EdgeMetallic.Value);
        asset.SetFloat("EdgeNormalStrength", EdgeNormalStrength.Value);
    }
}
