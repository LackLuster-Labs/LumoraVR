// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Backdrop for modal overlays: blurs or pixelates the screen behind the surface, then dims it toward
// TintColor.
[ComponentCategory("Assets/Materials")]
public class BlurMaterial : MaterialProvider
{
    public readonly Sync<BlurMode> Mode;
    // Pixels: the per-tap spread for Gaussian, the block size for Pixelate.
    public readonly Sync<float> Radius;
    // Alpha is how far the blurred result mixes toward the colour.
    public readonly Sync<colorHDR> TintColor;
    public readonly Sync<float> Opacity;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;
    public readonly Sync<Rect> Rect;
    public readonly Sync<bool> RectClip;

    // SCATTER. Iterations taps across Spread, a fraction of the screen per axis: on a ring at its edge, or
    // filling the disc when UsePoissonDisc. The magnitude texture scales the spread per pixel from red and
    // green, and the spread shrinks to nothing as the surface meets what is behind it, over DepthFadeDivisor
    // metres.
    public readonly Sync<int> Iterations;
    public readonly Sync<float2> Spread;
    public readonly AssetRef<TextureAsset> SpreadMagnitudeTexture;
    public readonly Sync<float2> SpreadTextureScale;
    public readonly Sync<float2> SpreadTextureOffset;
    public readonly Sync<bool> UsePoissonDisc;
    public readonly Sync<float> DepthFadeDivisor;
    // Bends what the scatter reads by the surface's normal, more for nearer surfaces.
    public readonly Sync<bool> Refract;
    public readonly Sync<float> RefractionStrength;
    public readonly AssetRef<TextureAsset> NormalMap;
    public readonly Sync<float2> NormalTextureScale;
    public readonly Sync<float2> NormalTextureOffset;

    // The defaults are the backdrop's: depth ignored, nothing written but colour, no stencil. Auto writes depth
    // for an opaque or cutout blur and none for a blended one.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    public readonly Sync<StencilComparison> StencilComparison;
    public readonly Sync<StencilOperation> StencilOperation;
    public readonly Sync<byte> StencilID;
    public readonly Sync<byte> StencilWriteMask;
    public readonly Sync<byte> StencilReadMask;

    // Whether the last update put a generated shader on in place of the shipped file.
    private bool _generatedVariant;

    protected override MaterialType MaterialType => MaterialType.Blur;

    public BlurMaterial()
    {
        Mode = new Sync<BlurMode>(this, BlurMode.Gaussian);
        Radius = new Sync<float>(this, 6f);
        TintColor = new Sync<colorHDR>(this, new colorHDR(0.03f, 0.03f, 0.06f, 0.55f));
        Opacity = new Sync<float>(this, 0.85f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        RenderQueue = new Sync<int>(this, 4050);
        Rect = new Sync<Rect>(this, default);
        RectClip = new Sync<bool>(this, false);

        Iterations = new Sync<int>(this, 10);
        Spread = new Sync<float2>(this, new float2(0.005f, 0.005f));
        SpreadMagnitudeTexture = new AssetRef<TextureAsset>(this);
        SpreadTextureScale = new Sync<float2>(this, float2.One);
        SpreadTextureOffset = new Sync<float2>(this, float2.Zero);
        UsePoissonDisc = new Sync<bool>(this, true);
        DepthFadeDivisor = new Sync<float>(this, 0.1f);
        Refract = new Sync<bool>(this, false);
        RefractionStrength = new Sync<float>(this, 0.05f);
        NormalMap = new AssetRef<TextureAsset>(this);
        NormalTextureScale = new Sync<float2>(this, float2.One);
        NormalTextureOffset = new Sync<float2>(this, float2.Zero);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Off);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.Always);
        StencilComparison = new Sync<StencilComparison>(this, Assets.StencilComparison.Always);
        StencilOperation = new Sync<StencilOperation>(this, Assets.StencilOperation.Keep);
        StencilID = new Sync<byte>(this, 0);
        StencilWriteMask = new Sync<byte>(this, byte.MaxValue);
        StencilReadMask = new Sync<byte>(this, byte.MaxValue);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        var rect = Rect.Value;
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        _generatedVariant = MaterialVariantSource.Apply(asset, VariantSource(), ShippedFile, _generatedVariant);
        asset.SetInt("BlurMode", (int)Mode.Value);
        asset.SetFloat("BlurRadius", Radius.Value);
        asset.SetColor("TintColor", TintColor.Value);
        asset.SetFloat("Opacity", Opacity.Value);
        asset.SetFloat4("Rect", new float4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        asset.SetBool("RectClip", RectClip.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);

        asset.SetInt("Iterations", Iterations.Value);
        asset.SetFloat2("Spread", Spread.Value);
        asset.SetTexture("SpreadMagnitudeTexture", SpreadMagnitudeTexture.Asset);
        asset.SetBool("UseSpreadMagnitudeTexture", SpreadMagnitudeTexture.Asset != null);
        asset.SetFloat2("SpreadTextureScale", SpreadTextureScale.Value);
        asset.SetFloat2("SpreadTextureOffset", SpreadTextureOffset.Value);
        asset.SetBool("UsePoissonDisc", UsePoissonDisc.Value);
        asset.SetFloat("DepthFadeDivisor", DepthFadeDivisor.Value);
        asset.SetBool("UseRefraction", Refract.Value);
        asset.SetFloat("RefractionStrength", RefractionStrength.Value);
        asset.SetTexture("NormalMap", NormalMap.Asset);
        asset.SetBool("UseNormalMap", NormalMap.Asset != null);
        asset.SetFloat2("NormalTextureScale", NormalTextureScale.Value);
        asset.SetFloat2("NormalTextureOffset", NormalTextureOffset.Value);
    }

    private const string ShippedFile = "res://Shaders/Blur.gdshader";

    // Null in the backdrop's state. Opaque, cutout and alpha all mix: the blur carries its own opacity, which an
    // opaque blur leaves at one.
    private string? VariantSource()
    {
        var blend = BlendMode.Value;
        bool writesDepth = ZWrite.Value switch
        {
            Assets.ZWrite.On => true,
            Assets.ZWrite.Off => false,
            _ => !RenderModeText.IsBlended(blend),
        };
        var test = ZTest.Value;
        string depthTest = RenderModeText.DepthTest(test);
        string stencil = RenderModeText.Stencil(StencilComparison.Value, StencilOperation.Value, StencilID.Value,
            StencilWriteMask.Value, StencilReadMask.Value);
        bool never = test == Assets.ZTest.Never || StencilComparison.Value == Assets.StencilComparison.Never;
        bool scatter = Mode.Value == BlurMode.Scatter;
        bool mixes = blend is not (Assets.BlendMode.Additive or Assets.BlendMode.Multiply or Assets.BlendMode.Transparent);
        if (mixes && !writesDepth && depthTest == "depth_test_disabled" && Culling.Value == Assets.Culling.None
            && stencil.Length == 0 && !never && !scatter)
            return null;

        var sb = new System.Text.StringBuilder("shader_type spatial;\nrender_mode unshaded, ");
        sb.Append(RenderModeText.Blend(blend)).Append(writesDepth ? ", depth_draw_always" : ", depth_draw_never");
        if (depthTest.Length > 0)
            sb.Append(", ").Append(depthTest);
        sb.Append(", ").Append(RenderModeText.Cull(Culling.Value)).Append(";\n");
        sb.Append(stencil);
        if (scatter)
            sb.Append("#define BLUR_SCATTER\n");
        if (never)
            sb.Append("#define BLUR_NEVER\n");
        sb.Append("#include \"res://Shaders/Blur_Core.gdshaderinc\"\n");
        return sb.ToString();
    }
}
