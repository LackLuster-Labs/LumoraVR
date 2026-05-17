// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

/// <summary>
/// Built-in grid floor material backed by res://Shaders/GridSpaceGround.gdshader.
/// </summary>
[ComponentCategory("Assets/Materials")]
public class GridSpaceGroundMaterial : MaterialProvider
{
    public readonly Sync<colorHDR> BaseNearColor;
    public readonly Sync<colorHDR> BaseFarColor;
    public readonly Sync<colorHDR> LineNearColor;
    public readonly Sync<colorHDR> LineFarColor;
    public readonly Sync<float> MinorScale;
    public readonly Sync<float> MajorScale;
    public readonly Sync<float> LineWidth;
    public readonly Sync<float> MajorLineWidth;
    public readonly Sync<float> RadialFade;
    public readonly Sync<BlendMode> BlendMode;
    public readonly Sync<Culling> Culling;
    public readonly Sync<int> RenderQueue;

    protected override MaterialType MaterialType => MaterialType.GridSpaceGround;

    public GridSpaceGroundMaterial()
    {
        BaseNearColor = new Sync<colorHDR>(this, new colorHDR(0.045f, 0.040f, 0.035f, 1f));
        BaseFarColor = new Sync<colorHDR>(this, new colorHDR(0.015f, 0.013f, 0.010f, 1f));
        LineNearColor = new Sync<colorHDR>(this, new colorHDR(0.34f, 0.58f, 0.98f, 1f));
        LineFarColor = new Sync<colorHDR>(this, new colorHDR(0.50f, 0.72f, 1.00f, 1f));
        MinorScale = new Sync<float>(this, 1.0f);
        MajorScale = new Sync<float>(this, 8.0f);
        LineWidth = new Sync<float>(this, 1.0f);
        MajorLineWidth = new Sync<float>(this, 1.6f);
        RadialFade = new Sync<float>(this, 1.1f);
        BlendMode = new Sync<BlendMode>(this, Assets.BlendMode.Opaque);
        Culling = new Sync<Culling>(this, Assets.Culling.Back);
        RenderQueue = new Sync<int>(this, -1);
    }

    protected override void UpdateMaterial(MaterialAsset asset)
    {
        asset.SetBlendMode(BlendMode.Value);
        asset.SetCulling(Culling.Value);
        asset.SetFloat("RenderQueue", RenderQueue.Value);

        asset.SetColor("BaseNearColor", BaseNearColor.Value);
        asset.SetColor("BaseFarColor", BaseFarColor.Value);
        asset.SetColor("LineNearColor", LineNearColor.Value);
        asset.SetColor("LineFarColor", LineFarColor.Value);
        asset.SetFloat("MinorScale", MinorScale.Value);
        asset.SetFloat("MajorScale", MajorScale.Value);
        asset.SetFloat("LineWidth", LineWidth.Value);
        asset.SetFloat("MajorLineWidth", MajorLineWidth.Value);
        asset.SetFloat("RadialFade", RadialFade.Value);
    }
}
