// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System;
using System.Text;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// World-space text material (nameplates, labels). Same glyph-coverage path as
// UITextMaterial but depth-tested and depth-writing via the Text_Unlit shader so foreground letters
// occlude later world UI as well as being hidden by nearer geometry. - xlinka
[ComponentCategory("Assets/Materials")]
public class TextMaterial : MaterialProvider, ICommonMaterial
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly Sync<float2> TextureScale;
    public readonly Sync<float2> TextureOffset;
    public readonly Sync<colorHDR> TintColor;
    public readonly Sync<bool> UseVertexColor;
    public readonly Sync<float> PixelRange;
    // Atlas is a distance field (RGB), not coverage (alpha). Drives the MSDF reconstruction branch in the
    // shader; set per-atlas by TextRenderer from atlas.IsMSDF. False keeps the plain coverage path. - xlinka
    public readonly Sync<bool> UseMSDF;

    // Optional glyph outline (readability for floating text). Thickness is in atlas texels;
    // 0 = no outline (unchanged glyph). Maps to outline_color / outline_thickness in the shader.
    // The face lies over the ring, so a face with almost no alpha leaves the glyph hollow.
    public readonly Sync<colorHDR> OutlineColor;
    public readonly Sync<float> OutlineThickness;
    // Atlas texels the face edge moves out (bolder, negative thinner) and texels of extra edge softening.
    // Distance-field atlases only.
    public readonly Sync<float> FaceDilate;
    public readonly Sync<float> FaceSoftness;
    // Strength of the light that sweeps across the letters, 0 for none, and its colour.
    public readonly Sync<float> Shimmer;
    public readonly Sync<colorHDR> ShimmerColor;
    public readonly Sync<bool> AlphaClip;
    public readonly Sync<float> AlphaCutoff;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    public readonly Sync<int> RenderQueue;
    // Culling counts from the side the text reads from: Back hides the side behind it, None (the default)
    // draws both. A colour mask without red, green and blue draws nothing while the stencil still goes, which
    // is how text cuts its letters out of something drawn after it.
    public readonly Sync<ColorMask> ColorMask;
    public readonly Sync<StencilComparison> StencilComparison;
    public readonly Sync<StencilOperation> StencilOperation;
    public readonly Sync<byte> StencilID;
    public readonly Sync<byte> StencilWriteMask;
    public readonly Sync<byte> StencilReadMask;
    // Depth bias: Factor scales the surface's depth slope, Units the smallest depth step. Negative pulls the
    // letters toward the camera, so text laid on a surface wins against it.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;
    // What fills each glyph's quad outside the letter. Automatic takes the face colour (the outline's when
    // there is one) at no alpha, so the edges fade without a fringe; otherwise BackgroundColor shows, and a
    // background with alpha draws a box behind every letter. Distance-field atlases only.
    public readonly Sync<bool> AutoBackgroundColor;
    public readonly Sync<colorHDR> BackgroundColor;

    private bool _variantOn;

    // Anything about this material changed. Text wearing it as its style (TextRenderer.Style) copies it again.
    public event Action<TextMaterial>? LookChanged;

    // Same DirectTexture override as UITextMaterial - binds a transient atlas
    // TextureAsset (no owning provider component) without an AssetRef. - xlinka
    public TextureAsset DirectTexture { get; set; } = null!;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.Text;

    public colorHDR Color
    {
        get => TintColor.Value;
        set => TintColor.Value = value;
    }

    public IAssetProvider<TextureAsset> MainTexture
    {
        get => Texture.Target;
        set => Texture.Target = value;
    }

    public TextMaterial()
    {
        Texture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        TintColor = new Sync<colorHDR>(this, colorHDR.White);
        UseVertexColor = new Sync<bool>(this, true);
        PixelRange = new Sync<float>(this, 8f);
        UseMSDF = new Sync<bool>(this, false);
        OutlineColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
        OutlineThickness = new Sync<float>(this, 0f);
        FaceDilate = new Sync<float>(this, 0f);
        FaceSoftness = new Sync<float>(this, 0f);
        Shimmer = new Sync<float>(this, 0f);
        ShimmerColor = new Sync<colorHDR>(this, colorHDR.White);
        AlphaClip = new Sync<bool>(this, true);
        AlphaCutoff = new Sync<float>(this, 0.5f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        RenderQueue = new Sync<int>(this, 0);
        ColorMask = new Sync<ColorMask>(this, Assets.ColorMask.RGBA);
        StencilComparison = new Sync<StencilComparison>(this, Assets.StencilComparison.Always);
        StencilOperation = new Sync<StencilOperation>(this, Assets.StencilOperation.Keep);
        StencilID = new Sync<byte>(this, 0);
        StencilWriteMask = new Sync<byte>(this, byte.MaxValue);
        StencilReadMask = new Sync<byte>(this, byte.MaxValue);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        AutoBackgroundColor = new Sync<bool>(this, true);
        BackgroundColor = new Sync<colorHDR>(this, new colorHDR(0f, 0f, 0f, 0f));
    }

    public override void OnChanges()
    {
        base.OnChanges();
        LookChanged?.Invoke(this);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetTexture("Texture", DirectTexture ?? Texture.Asset);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);
        asset.SetColor("TintColor", TintColor.Value);
        asset.SetBool("UseVertexColor", UseVertexColor.Value);
        asset.SetFloat("PixelRange", PixelRange.Value);
        asset.SetBool("UseMSDF", UseMSDF.Value);
        asset.SetColor("OutlineColor", OutlineColor.Value);
        asset.SetFloat("OutlineThickness", OutlineThickness.Value);
        asset.SetFloat("FaceDilate", FaceDilate.Value);
        asset.SetFloat("FaceSoftness", FaceSoftness.Value);
        asset.SetFloat("Shimmer", Shimmer.Value);
        asset.SetColor("ShimmerColor", ShimmerColor.Value);
        asset.SetBool("AlphaClip", AlphaClip.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("ZWrite", (int)ZWrite.Value);
        asset.SetInt("ZTest", (int)ZTest.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
        asset.SetInt("ColorMask", (int)ColorMask.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
        asset.SetBool("UseBackgroundColor", !AutoBackgroundColor.Value);
        asset.SetColor("BackgroundColor", BackgroundColor.Value);
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        _variantOn = MaterialVariantSource.Apply(asset,
            VariantSource(Culling.Value, ColorMask.Value, StencilComparison.Value, StencilOperation.Value,
                StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value, ZWrite.Value, ZTest.Value, depthOffset),
            "res://Shaders/Text_Unlit.gdshader", _variantOn);
    }

    // Depth writing and testing, culling, a depth bias and the stencil are compile-time render state.
    // Text_Unlit.gdshader draws both faces with no stencil, which is nearly all text; any other state gets a
    // few lines of source over the same body, compiled once per distinct text. Godot's stencil keeps one
    // reference value for the test and the write and stores only that value, so the masks fold into it, and an
    // operation other than Replace stores what it would leave in a cleared buffer: exact for one stamp on a
    // cleared buffer, the usual use. A write marks the letters only (TEXT_STAMP), not the quads they sit on.
    // Text that skips the depth test draws over everything, with no bias to apply; Godot tests nearer-or-equal
    // or not at all, so Less reads as the default. -xlinka
    internal static string? VariantSource(Culling culling, ColorMask colorMask, StencilComparison comparison,
        StencilOperation operation, byte id, byte writeMask, byte readMask, ZWrite zWrite = Assets.ZWrite.Auto,
        ZTest zTest = Assets.ZTest.LessOrEqual, bool depthOffset = false)
    {
        bool never = comparison == Assets.StencilComparison.Never;
        bool enabled = !never && comparison != Assets.StencilComparison.Disabled;
        bool read = enabled && comparison != Assets.StencilComparison.Always;
        bool write = enabled && operation != Assets.StencilOperation.Keep;
        bool hidden = never || (colorMask & Assets.ColorMask.RGB) == Assets.ColorMask.None;
        bool depth = zWrite == Assets.ZWrite.On || (zWrite == Assets.ZWrite.Auto && !hidden);
        bool overAll = zTest is Assets.ZTest.Disabled or Assets.ZTest.Always;
        bool bias = depthOffset && !overAll;
        if (culling == Assets.Culling.None && !read && !write && !hidden && depth && !overAll && !bias)
            return null;

        var sb = new StringBuilder("shader_type spatial;\nrender_mode unshaded, blend_mix, ", 200);
        sb.Append(depth ? "depth_draw_always, " : "depth_draw_never, ");
        if (overAll)
            sb.Append("depth_test_disabled, ");
        sb.Append(culling switch
        {
            Assets.Culling.Back => "cull_front",
            Assets.Culling.Front => "cull_back",
            _ => "cull_disabled",
        }).Append(";\n");
        if (read || write)
        {
            int reference = read ? id & readMask : StoredValue(operation, id) & writeMask;
            sb.Append("stencil_mode ");
            if (read)
                sb.Append("read, ");
            if (write)
                sb.Append("write, ");
            sb.Append(CompareMode(comparison)).Append(", ").Append(reference).Append(";\n");
        }
        if (write)
            sb.Append("#define TEXT_STAMP\n");
        if (hidden)
            sb.Append("#define TEXT_HIDDEN\n");
        if (bias)
            sb.Append("#define DEPTH_OFFSET\n");
        sb.Append("#include \"res://Shaders/Text_Unlit_Core.gdshaderinc\"\n");
        return sb.ToString();
    }

    private static int StoredValue(StencilOperation operation, byte id) => operation switch
    {
        Assets.StencilOperation.Zero => 0,
        Assets.StencilOperation.IncrementSaturate or Assets.StencilOperation.IncrementWrap => 1,
        Assets.StencilOperation.DecrementSaturate => 0,
        Assets.StencilOperation.DecrementWrap or Assets.StencilOperation.Invert => 255,
        _ => id
    };

    private static string CompareMode(StencilComparison comparison) => comparison switch
    {
        Assets.StencilComparison.Less => "compare_less",
        Assets.StencilComparison.Equal => "compare_equal",
        Assets.StencilComparison.LessOrEqual => "compare_less_or_equal",
        Assets.StencilComparison.Greater => "compare_greater",
        Assets.StencilComparison.NotEqual => "compare_not_equal",
        Assets.StencilComparison.GreaterOrEqual => "compare_greater_or_equal",
        _ => "compare_always"
    };
}
