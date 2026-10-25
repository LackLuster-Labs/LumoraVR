// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Shows how far away whatever is BEHIND the surface is, as grey, in place of its colour: metres along the
// view, times Multiply, plus Offset, held to black..white. A fresh one shades the first metre and paints
// everything past it white; a Multiply of 0.1 spreads the shades over ten metres. With Clip on, ClipStart to
// ClipEnd metres is stretched over the range first, so the shades can sit on just the distance that matters.
//
// Only the opaque scene has depth, so glass and other transparent surfaces are looked through. The rendering
// and canvas members are the same on every screen filter (ScreenFilterShader). Drawn after the transparent
// band. -xlinka
[ComponentCategory("Assets/Materials")]
public class DepthMaterial : MaterialProvider
{
    public readonly Sync<float> Multiply;
    public readonly Sync<float> Offset;
    public readonly Sync<bool> Clip;
    public readonly Sync<float> ClipStart;
    public readonly Sync<float> ClipEnd;

    // Additive and multiply combine the grey with the colour behind it.
    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    // Auto writes no depth.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    // -1 draws at 3500, after ordinary transparent surfaces.
    public readonly Sync<int> RenderQueue;

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

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public override bool UseLoadingPlaceholder => false;

    public DepthMaterial()
    {
        Multiply = new Sync<float>(this, 1f);
        Offset = new Sync<float>(this, 0f);
        Clip = new Sync<bool>(this, false);
        ClipStart = new Sync<float>(this, 0f);
        ClipEnd = new Sync<float>(this, 10f);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        RenderQueue = new Sync<int>(this, -1);

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
        ScreenFilterShader.Apply(asset, "Depth", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 3500, opaqueWritesDepth: false);
        asset.SetFloat("DepthMultiply", Multiply.Value);
        asset.SetFloat("DepthOffset", Offset.Value);
        asset.SetBool("UseClipRange", Clip.Value);
        asset.SetFloat("ClipStart", ClipStart.Value);
        asset.SetFloat("ClipEnd", ClipEnd.Value);
    }
}
