// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A hue, saturation and value shift over whatever is BEHIND the surface. Each channel is multiplied,
// then offset: hue wraps round, saturation clamps to 0..1, value is left alone so a bright scene stays
// bright. All three are 0..1, so a hue offset of 0.5 is the opposite colour. A fresh one shifts all
// three by 0.2, which is visibly doing something rather than an invisible pane.
//
// Drawn after the transparent band, over everything it filters. -xlinka
[ComponentCategory("Assets/Materials")]
public class HSVMaterial : MaterialProvider
{
    public readonly Sync<float3> HSV_Offset;
    public readonly Sync<float3> HSV_Multiply;

    // Additive and multiply combine the shifted colour with the unshifted one behind it.
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

    protected override MaterialType MaterialType => MaterialType.HSV;

    public override bool UseLoadingPlaceholder => false;

    public HSVMaterial()
    {
        HSV_Offset = new Sync<float3>(this, new float3(0.2f, 0.2f, 0.2f));
        HSV_Multiply = new Sync<float3>(this, new float3(1f, 1f, 1f));
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
        ScreenFilterShader.Apply(asset, "HSV", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 3500, opaqueWritesDepth: false);
        asset.SetFloat3("HsvOffset", HSV_Offset.Value);
        asset.SetFloat3("HsvMultiply", HSV_Multiply.Value);
    }
}
