// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A colour grade over whatever is BEHIND the surface, through a lookup table: film looks, night
// vision, a sepia room. Lerp blends toward SecondaryLUT, so a grade can fade in.
//
// THE TABLE IS A 2D STRIP, N slices of N by N side by side (256 by 16, 1024 by 32): red across a
// slice, green down it, blue picking the slice. There are no 3D textures here, and the strip is the
// layout grading tools export for exactly that reason. With no table set the colour passes through.
//
// UseSRGB looks the colour up gamma encoded, which is what a table made by grading an image expects;
// off, the table is read as linear in and linear out.
//
// Drawn after the transparent band, over everything it filters. -xlinka
[ComponentCategory("Assets/Materials")]
public class LUTMaterial : MaterialProvider
{
    public readonly AssetRef<TextureAsset> LUT;
    public readonly AssetRef<TextureAsset> SecondaryLUT;
    public readonly Sync<bool> UseSRGB;

    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Lerp;

    // Additive and multiply combine the graded colour with the ungraded one behind it.
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;

    // -1 draws at 3500, after ordinary transparent surfaces.
    public readonly Sync<int> RenderQueue;

    // Auto writes no depth. ZTest picks when the filter draws against what is already there.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;

    // For use inside a UI: a clip rect in the mesh's own x and y, the channels written, the stencil test.
    [Group("Canvas")]
    public readonly Sync<Rect> Rect;
    public readonly Sync<bool> RectClip;
    public readonly Sync<ColorMask> ColorMask;
    public readonly Sync<StencilComparison> StencilComparison;
    public readonly Sync<StencilOperation> StencilOperation;
    public readonly Sync<byte> StencilID;
    public readonly Sync<byte> StencilWriteMask;
    public readonly Sync<byte> StencilReadMask;

    protected override MaterialType MaterialType => MaterialType.LUT;

    public override bool UseLoadingPlaceholder => false;

    public LUTMaterial()
    {
        LUT = new AssetRef<TextureAsset>(this);
        SecondaryLUT = new AssetRef<TextureAsset>(this);
        UseSRGB = new Sync<bool>(this, true);
        Lerp = new Sync<float>(this, 0f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);

        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        Rect = new Sync<Rect>(this, Lumora.Core.Math.Rect.Zero);
        RectClip = new Sync<bool>(this, false);
        ColorMask = new Sync<ColorMask>(this, Assets.ColorMask.RGBA);
        StencilComparison = new Sync<StencilComparison>(this, Assets.StencilComparison.Always);
        StencilOperation = new Sync<StencilOperation>(this, Assets.StencilOperation.Keep);
        StencilID = new Sync<byte>(this, 0);
        StencilWriteMask = new Sync<byte>(this, byte.MaxValue);
        StencilReadMask = new Sync<byte>(this, byte.MaxValue);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        ScreenFilterShader.Apply(asset, "LUT", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 3500, opaqueWritesDepth: false);
        asset.SetTexture("LutTexture", LUT.Asset);
        asset.SetTexture("SecondaryLutTexture", SecondaryLUT.Asset);
        asset.SetBool("UseSrgb", UseSRGB.Value);
        asset.SetFloat("LutLerp", Lerp.Value);
    }
}
