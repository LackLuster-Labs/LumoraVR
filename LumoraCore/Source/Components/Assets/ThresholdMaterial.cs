// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Cuts whatever is BEHIND the surface to two levels per channel: below Threshold goes dark, above it
// goes full, with a ramp Transition wide between the two. Per channel, so a coloured scene comes out
// in the eight primaries rather than black and white.
//
// Drawn after the transparent band, over everything it filters. -xlinka
[ComponentCategory("Assets/Materials")]
public class ThresholdMaterial : MaterialProvider
{
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Threshold;

    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Transition;

    // Additive and multiply combine the cut colour with the uncut one behind it.
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

    protected override MaterialType MaterialType => MaterialType.Threshold;

    public override bool UseLoadingPlaceholder => false;

    public ThresholdMaterial()
    {
        Threshold = new Sync<float>(this, 0.5f);
        Transition = new Sync<float>(this, 0.005f);
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
        ScreenFilterShader.Apply(asset, "Threshold", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 3500, opaqueWritesDepth: false);
        asset.SetFloat("Threshold", Threshold.Value);
        asset.SetFloat("Transition", Transition.Value);
    }
}
