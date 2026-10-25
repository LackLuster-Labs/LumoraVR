// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using System.Text;
using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// How a UI material's mask picture cuts it: its alpha multiplied in, or a hard edge at the alpha cutoff.
public enum UIMaskMode
{
    Multiply,
    Clip
}

[ComponentCategory("Assets/Materials/UI")]
public class UIUnlitMaterial : MaterialProvider, ICommonMaterial
{
    public readonly AssetRef<TextureAsset> Texture;
    public readonly Sync<float2> TextureScale;
    public readonly Sync<float2> TextureOffset;
    public readonly Sync<colorHDR> TintColor;
    public readonly Sync<bool> UseVertexColor;
    public readonly Sync<bool> AlphaClip;
    public readonly Sync<float> AlphaCutoff;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    public readonly Sync<int> RenderQueue;
    public readonly Sync<Rect> Rect;
    public readonly Sync<bool> RectClip;
    public readonly Sync<float2> ClipOffset;
    public readonly Sync<ColorMask> ColorMask;
    public readonly Sync<StencilComparison> StencilComparison;
    public readonly Sync<StencilOperation> StencilOperation;
    public readonly Sync<byte> StencilID;
    public readonly Sync<byte> StencilWriteMask;
    public readonly Sync<byte> StencilReadMask;
    // A second picture whose alpha cuts this one, laid on the quad by its own scale and offset.
    public readonly AssetRef<TextureAsset> MaskTexture;
    public readonly Sync<float2> MaskScale;
    public readonly Sync<float2> MaskOffset;
    public readonly Sync<UIMaskMode> MaskMode;
    // Depth bias: Factor scales the surface's depth slope, Units the smallest depth step. Negative pulls the
    // surface toward the camera. Zero for both leaves the depth as drawn.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;
    // Where the world's opaque surfaces stand in front of it, what does draw (a material that skips the depth
    // test) is multiplied by HiddenTint, so a panel seen through a wall reads as behind it.
    public readonly Sync<bool> TintHidden;
    public readonly Sync<colorHDR> HiddenTint;
    public readonly Sync<UITextureMode> TextureMode;
    // Bound to a frame sheet (an animated picture), the material plays it; off, it holds the first frame.
    public readonly Sync<bool> PlayFrames;

    private bool _variantOn;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.UI_Unlit;

    // Texture override that bypasses the AssetRef provider chain - used by text submeshes
    // where the font atlas is a transient `TextureAsset` with no owning provider component.
    // Bakes the atlas directly into the material instead of routing through a property block. - xlinka
    public TextureAsset DirectTexture { get; set; } = null!;

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

    public UIUnlitMaterial()
    {
        Texture = new AssetRef<TextureAsset>(this);
        TextureScale = new Sync<float2>(this, float2.One);
        TextureOffset = new Sync<float2>(this, float2.Zero);
        TintColor = new Sync<colorHDR>(this, colorHDR.White);
        UseVertexColor = new Sync<bool>(this, true);
        // A hard cut: anything fainter than AlphaCutoff is not drawn, depth included. Off by default; the
        // texture's own alpha already shapes a picture, and a soft edge fading out is what UI wants. -xlinka
        AlphaClip = new Sync<bool>(this, false);
        AlphaCutoff = new Sync<float>(this, 0.01f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
        Culling = new Sync<Culling>(this, Assets.Culling.None);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        RenderQueue = new Sync<int>(this, -1);
        Rect = new Sync<Rect>(this, Lumora.Core.Math.Rect.Zero);
        RectClip = new Sync<bool>(this, false);
        ClipOffset = new Sync<float2>(this, float2.Zero);
        ColorMask = new Sync<ColorMask>(this, Assets.ColorMask.RGBA);
        StencilComparison = new Sync<StencilComparison>(this, Assets.StencilComparison.Always);
        StencilOperation = new Sync<StencilOperation>(this, Assets.StencilOperation.Keep);
        StencilID = new Sync<byte>(this, 0);
        StencilWriteMask = new Sync<byte>(this, byte.MaxValue);
        StencilReadMask = new Sync<byte>(this, byte.MaxValue);
        MaskTexture = new AssetRef<TextureAsset>(this);
        MaskScale = new Sync<float2>(this, float2.One);
        MaskOffset = new Sync<float2>(this, float2.Zero);
        MaskMode = new Sync<UIMaskMode>(this, UIMaskMode.Multiply);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        TintHidden = new Sync<bool>(this, false);
        HiddenTint = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 0.73f));
        TextureMode = new Sync<UITextureMode>(this, UITextureMode.Color);
        PlayFrames = new Sync<bool>(this, true);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        var rect = Rect.Value;

        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetTexture("Texture", DirectTexture ?? Texture.Asset);
        if (!PlayFrames.Value)
            asset.SetInt("FrameCount", 1);
        asset.SetFloat2("TextureScale", TextureScale.Value);
        asset.SetFloat2("TextureOffset", TextureOffset.Value);
        asset.SetColor("TintColor", TintColor.Value);
        asset.SetBool("UseVertexColor", UseVertexColor.Value);
        asset.SetBool("AlphaClip", AlphaClip.Value);
        asset.SetFloat("AlphaCutoff", AlphaCutoff.Value);
        asset.SetInt("ZWrite", (int)ZWrite.Value);
        asset.SetInt("ZTest", (int)ZTest.Value);
        asset.SetInt("RenderQueue", RenderQueue.Value);
        asset.SetFloat4("Rect", new float4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        asset.SetBool("RectClip", RectClip.Value);
        asset.SetFloat2("ClipOffset", ClipOffset.Value);
        asset.SetInt("ColorMask", (int)ColorMask.Value);
        asset.SetInt("StencilComparison", (int)StencilComparison.Value);
        asset.SetInt("StencilOperation", (int)StencilOperation.Value);
        asset.SetInt("StencilID", StencilID.Value);
        asset.SetInt("StencilWriteMask", StencilWriteMask.Value);
        asset.SetInt("StencilReadMask", StencilReadMask.Value);
        asset.SetTexture("MaskTexture", MaskTexture.Asset);
        asset.SetFloat2("MaskScale", MaskScale.Value);
        asset.SetFloat2("MaskOffset", MaskOffset.Value);
        asset.SetInt("MaskMode", (int)MaskMode.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);
        asset.SetColor("HiddenTint", HiddenTint.Value);
        asset.SetInt("TextureMode", (int)TextureMode.Value);

        // The stencil variants each name a shader of its own and keep it.
        if (MaterialType == MaterialType.UI_Unlit)
        {
            bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
            _variantOn = MaterialVariantSource.Apply(asset,
                VariantSource(Culling.Value, ZWrite.Value, ZTest.Value, depthOffset, TintHidden.Value),
                ShippedFile(ZWrite.Value, ZTest.Value), _variantOn);
        }
    }

    // Culling, depth write, depth test, a depth bias and the hidden tint (which reads the scene's depth, and
    // so costs a depth copy only where it is asked for) are compile-time render state. Three shipped shaders
    // cover what a canvas needs on its own: read from its face only, a two-sided backing that writes depth,
    // and an overlay that skips the depth test (and so shows both sides). Any other state gets a few lines of
    // source over the same body, compiled once per distinct text however many materials wear it.
    //
    // Culling is counted from the side a canvas is read from: Back hides the side behind it, Front shows
    // only that side, None shows both. Helio winds the reading side as Godot's back face, which is why Back
    // is cull_front. -xlinka
    internal static string? VariantSource(Culling culling, ZWrite zWrite, ZTest zTest, bool depthOffset, bool tintHidden = false)
    {
        bool overlay = zTest is Assets.ZTest.Disabled or Assets.ZTest.Always;
        bool writes = zWrite == Assets.ZWrite.On;
        if (!depthOffset && !tintHidden && (overlay || (writes && culling == Assets.Culling.None) || (!writes && culling == Assets.Culling.Back)))
            return null;

        var sb = new StringBuilder("shader_type spatial;\nrender_mode unshaded, blend_mix, ", 160);
        sb.Append(writes ? "depth_draw_always" : "depth_draw_never");
        if (overlay)
            sb.Append(", depth_test_disabled");
        sb.Append(", ").Append(overlay ? "cull_disabled" : culling switch
        {
            Assets.Culling.Back => "cull_front",
            Assets.Culling.Front => "cull_back",
            _ => "cull_disabled",
        }).Append(";\n");
        if (tintHidden)
            sb.Append("#define HIDDEN_TINT\n");
        if (depthOffset)
            sb.Append("#define DEPTH_OFFSET\n");
        else if (writes)
            sb.Append("#define BACKING_PUSH\n");
        sb.Append("#include \"res://Shaders/UI_Unlit_Core.gdshaderinc\"\n");
        return sb.ToString();
    }

    internal static string ShippedFile(ZWrite zWrite, ZTest zTest)
        => zTest is Assets.ZTest.Disabled or Assets.ZTest.Always ? "res://Shaders/UI_UnlitOverlay.gdshader"
            : zWrite == Assets.ZWrite.On ? "res://Shaders/UI_UnlitZWrite.gdshader"
            : "res://Shaders/UI_Unlit.gdshader";
}
