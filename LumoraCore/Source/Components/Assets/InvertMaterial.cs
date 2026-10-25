// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Inverts whatever is BEHIND the surface, a photographic negative: white goes black, red goes cyan. Lerp is
// how far, so a half is a flat grey wash and anything between fades the effect in.
//
// The rendering and canvas members are the same on every screen filter (ScreenFilterShader). Drawn after the
// transparent band, over everything it filters. -xlinka
[ComponentCategory("Assets/Materials")]
public class InvertMaterial : MaterialProvider
{
    [Range(0f, 1f, "0.00")]
    public readonly Sync<float> Lerp;

    // Additive and multiply combine the filtered colour with the unfiltered one behind it.
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

    public InvertMaterial()
    {
        Lerp = new Sync<float>(this, 1f);

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
        ScreenFilterShader.Apply(asset, "Invert", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 3500, opaqueWritesDepth: false);
        asset.SetFloat("LerpAmount", Lerp.Value);
    }
}
