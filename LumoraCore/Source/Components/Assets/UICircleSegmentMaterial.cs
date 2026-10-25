// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// Ring segments for a radial menu, drawn on quads that carry each segment's shape per vertex, so one material
// draws every segment however they differ: the point relative to the centre in the first UV set, the start
// angle and sweep in the second, the inner and outer radius in the third, the outline thickness and corner
// rounding in the fourth, and the fill in the vertex colour (UICircleSegment_Core). FillTint multiplies the
// fill; the outline is OutlineTint. With Overlay, whatever part of a segment the scene hides is drawn tinted by
// OverlayTint, for a menu seen through a wall with the depth test off.
//
// Rect clips in the mesh's own XY when RectClip is on. The stencil follows the same rules as the other UI
// materials. -xlinka
[ComponentCategory("Assets/Materials/UI")]
public class UICircleSegmentMaterial : MaterialProvider
{
    public readonly Sync<colorHDR> FillTint;
    public readonly Sync<colorHDR> OutlineTint;
    public readonly Sync<bool> Overlay;
    public readonly Sync<colorHDR> OverlayTint;

    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    public readonly Sync<ZTest> ZTest;
    // Depth bias: negative pulls the surface toward the camera in depth only.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;
    // -1 = default
    public readonly Sync<int> RenderQueue;

    [Group("Clip and stencil")]
    public readonly Sync<Rect> Rect;
    public readonly Sync<bool> RectClip;
    public readonly Sync<ColorMask> ColorMask;
    public readonly Sync<StencilComparison> StencilComparison;
    public readonly Sync<StencilOperation> StencilOperation;
    public readonly Sync<byte> StencilID;
    public readonly Sync<byte> StencilWriteMask;
    public readonly Sync<byte> StencilReadMask;

    private const string ShippedFile = "res://Shaders/Mat_UICircleSegment.gdshader";
    private string? _stencil;
    private (BlendMode, ZWrite, ZTest, Culling, bool, bool, string?) _state;
    private bool _stateSet;
    private string? _source;

    public override bool UseLoadingPlaceholder => false;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public UICircleSegmentMaterial()
    {
        FillTint = new Sync<colorHDR>(this, colorHDR.White);
        OutlineTint = new Sync<colorHDR>(this, colorHDR.White);
        Overlay = new Sync<bool>(this, false);
        OverlayTint = new Sync<colorHDR>(this, new colorHDR(1f, 1f, 1f, 0.73f));

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Alpha);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        ZTest = new Sync<ZTest>(this, Assets.ZTest.LessOrEqual);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
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

    public override bool TryGetPrimaryColor(out colorHDR color)
    {
        color = FillTint.Value;
        return true;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        // The overlay reads the scene's depth, which only a blended surface has drawn behind it.
        bool overlay = Overlay.Value && SpecialMaterialSource.IsBlended(BlendMode.Value);
        _stencil = SpecialMaterialSource.Stencil(StencilComparison.Value, StencilOperation.Value, StencilID.Value,
            StencilReadMask.Value, StencilWriteMask.Value);
        var state = (BlendMode.Value, ZWrite.Value, ZTest.Value, Culling.Value, depthOffset, overlay, _stencil);
        if (!_stateSet || state != _state)
        {
            _stateSet = true;
            _state = state;
            bool shipped = BlendMode.Value == Assets.BlendMode.Alpha && ZWrite.Value == Assets.ZWrite.Auto
                && (ZTest.Value is Assets.ZTest.LessOrEqual or Assets.ZTest.Less) && Culling.Value == Assets.Culling.Back
                && !depthOffset && !overlay && _stencil == null;
            _source = shipped ? null : SpecialMaterialSource.Unlit("UICircleSegment_Core", BlendMode.Value, ZWrite.Value,
                ZTest.Value, Culling.Value, depthOffset, _stencil, overlay ? "SEGMENT_OVERLAY" : null);
        }
        SpecialMaterialSource.Wear(asset, _source, ShippedFile);
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        asset.SetColor("FillTint", FillTint.Value);
        asset.SetColor("OutlineTint", OutlineTint.Value);
        asset.SetColor("OverlayTint", OverlayTint.Value);

        var rect = Rect.Value;
        asset.SetBool("RectClip", RectClip.Value);
        asset.SetFloat4("Rect", new float4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        asset.SetInt("ColorMask", (int)ColorMask.Value);
    }
}
