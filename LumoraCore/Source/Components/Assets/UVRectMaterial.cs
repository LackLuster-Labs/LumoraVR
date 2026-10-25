// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// A rectangle marked out on the surface's UVs: InnerColor inside Rect, OuterColor outside, a hard edge between.
// With UseClipRect everything outside ClipRect is cut away as well. Both rects are in the UV square, V counted
// down from the top like every texture here. Unlit; a frame, a selection box, a progress bar on a quad. -xlinka
[ComponentCategory("Assets/Materials")]
public class UVRectMaterial : MaterialProvider
{
    public readonly Sync<Rect> Rect;
    public readonly Sync<colorHDR> OuterColor;
    public readonly Sync<colorHDR> InnerColor;

    public readonly Sync<bool> UseClipRect;
    public readonly Sync<Rect> ClipRect;

    [Group("Rendering")]
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<ZWrite> ZWrite;
    // Depth bias: negative pulls the surface toward the camera in depth only.
    public readonly Sync<float> OffsetFactor;
    public readonly Sync<float> OffsetUnits;
    // -1 = default
    public readonly Sync<int> RenderQueue;

    private const string ShippedFile = "res://Shaders/Mat_UVRect.gdshader";
    private (BlendMode, ZWrite, Culling, bool, bool) _state;
    private bool _stateSet;
    private string? _source;

    protected override MaterialType MaterialType => MaterialType.NamedShader;

    public UVRectMaterial()
    {
        Rect = new Sync<Rect>(this, new Rect(0.25f, 0.25f, 0.5f, 0.5f));
        OuterColor = new Sync<colorHDR>(this, colorHDR.Black);
        InnerColor = new Sync<colorHDR>(this, colorHDR.White);
        UseClipRect = new Sync<bool>(this, false);
        ClipRect = new Sync<Rect>(this, Lumora.Core.Math.Rect.UnitRect);

        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        ZWrite = new Sync<ZWrite>(this, Assets.ZWrite.Auto);
        OffsetFactor = new Sync<float>(this, 0f);
        OffsetUnits = new Sync<float>(this, 0f);
        RenderQueue = new Sync<int>(this, -1);
    }

    public override bool TryGetPrimaryColor(out colorHDR color)
    {
        color = InnerColor.Value;
        return true;
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        bool depthOffset = OffsetFactor.Value != 0f || OffsetUnits.Value != 0f;
        var state = (BlendMode.Value, ZWrite.Value, Culling.Value, depthOffset, UseClipRect.Value);
        if (!_stateSet || state != _state)
        {
            _stateSet = true;
            _state = state;
            _source = state == (Assets.BlendMode.Opaque, Assets.ZWrite.Auto, Assets.Culling.Back, false, false)
                ? null
                : SpecialMaterialSource.Unlit("UVRect_Core", BlendMode.Value, ZWrite.Value, ZTest.LessOrEqual, Culling.Value,
                    depthOffset, define: UseClipRect.Value ? "RECT_CLIP" : null);
        }
        SpecialMaterialSource.Wear(asset, _source, ShippedFile);
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);
        asset.SetFloat("OffsetFactor", OffsetFactor.Value);
        asset.SetFloat("OffsetUnits", OffsetUnits.Value);

        var rect = Rect.Value;
        var clip = ClipRect.Value;
        asset.SetFloat4("Rect", new float4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        asset.SetFloat4("ClipRect", new float4(clip.xMin, clip.yMin, clip.xMax, clip.yMax));
        asset.SetColor("OuterColor", OuterColor.Value);
        asset.SetColor("InnerColor", InnerColor.Value);
    }
}
