// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Pixelates whatever is BEHIND the surface: the screen cut into Resolution blocks across and down, each one
// flat. The count is of the whole screen, not of the surface, so a pane across the room and one in front of
// the face make blocks of the same size.
//
// ResolutionMagnitudeTexture scales the count over the surface, red across and green down, with its own
// tiling: a gradient toward the rim makes blocks that grow toward the edge of the pane.
//
// Unlike the other filters it draws before the transparent band and writes depth when opaque (ZWrite Auto),
// so transparent surfaces behind it stay hidden rather than drawing unpixelated over it, and those in front
// still draw over it. The rendering and canvas members are the same on every screen filter
// (ScreenFilterShader). -xlinka
[ComponentCategory("Assets/Materials")]
public class PixelateMaterial : MaterialProvider
{
    public readonly Sync<float2> Resolution;
    public readonly AssetRef<TextureAsset> ResolutionMagnitudeTexture;
    public readonly Sync<float2> ResolutionTextureScale;
    public readonly Sync<float2> ResolutionTextureOffset;

    // Additive and multiply combine the filtered colour with the unfiltered one behind it.
    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    // Auto writes depth when opaque or cutout.
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    // -1 draws at 2900, just before ordinary transparent surfaces.
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

    public PixelateMaterial()
    {
        Resolution = new Sync<float2>(this, new float2(100f, 100f));
        ResolutionMagnitudeTexture = new AssetRef<TextureAsset>(this);
        ResolutionTextureScale = new Sync<float2>(this, float2.One);
        ResolutionTextureOffset = new Sync<float2>(this, float2.Zero);

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
        ScreenFilterShader.Apply(asset, "Pixelate", new ScreenFilterState(BlendMode.Value, Culling.Value, ZWrite.Value,
            ZTest.Value, RenderQueue.Value, Rect.Value, RectClip.Value, ColorMask.Value, StencilComparison.Value,
            StencilOperation.Value, StencilID.Value, StencilWriteMask.Value, StencilReadMask.Value), 2900, opaqueWritesDepth: true);
        asset.SetFloat2("Resolution", Resolution.Value);
        asset.SetTexture("ResolutionTexture", ResolutionMagnitudeTexture.Asset);
        var scale = ResolutionTextureScale.Value;
        var offset = ResolutionTextureOffset.Value;
        asset.SetFloat4("ResolutionTextureTransform", new float4(scale.x, scale.y, offset.x, offset.y));
    }
}
