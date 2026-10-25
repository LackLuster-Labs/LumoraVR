// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// PBS that lights up where it meets other geometry: force fields, water lines, shields touching the floor.
// The gap between this surface and whatever is drawn behind it is read off the depth buffer, in metres; the
// albedo and emission swing to the intersect colours as the gap closes in across the begin pair and back out
// across the end pair. A pair with no width is a hard edge.
//
// Reading the depth buffer means drawing after the opaque world, so this is always a blended surface, and it
// writes no depth of its own: it would otherwise read itself back as the thing it intersects. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_IntersectGlow : PBS_SurfaceBase
{
    [Group("Intersect")]
    public readonly Sync<colorHDR> IntersectAlbedoColor;

    [Group("Intersect")]
    public readonly Sync<colorHDR> IntersectEmissiveColor;

    [Group("Intersect")]
    public readonly Sync<float> BeginTransitionStart;

    [Group("Intersect")]
    public readonly Sync<float> BeginTransitionEnd;

    [Group("Intersect")]
    public readonly Sync<float> EndTransitionStart;

    [Group("Intersect")]
    public readonly Sync<float> EndTransitionEnd;

    protected override PBSSurfaceFeatures Features => PBSSurfaceFeatures.Intersect;

    public PBS_IntersectGlow() : base(Assets.Culling.Back, Assets.AlphaHandling.AlphaBlend)
    {
        IntersectAlbedoColor = new Sync<colorHDR>(this, colorHDR.White);
        IntersectEmissiveColor = new Sync<colorHDR>(this, new colorHDR(1.2f, 0.8f, 0.8f, 1f));
        BeginTransitionStart = new Sync<float>(this, 0f);
        BeginTransitionEnd = new Sync<float>(this, 0f);
        EndTransitionStart = new Sync<float>(this, 0.01f);
        EndTransitionEnd = new Sync<float>(this, 0.01f);
    }

    protected override void UpdateEffect(MaterialAsset asset)
    {
        asset.SetColor("IntersectAlbedoColor", IntersectAlbedoColor.Value);
        asset.SetColor("IntersectEmissiveColor", IntersectEmissiveColor.Value);
        asset.SetFloat("BeginTransitionStart", BeginTransitionStart.Value);
        asset.SetFloat("BeginTransitionEnd", BeginTransitionEnd.Value);
        asset.SetFloat("EndTransitionStart", EndTransitionStart.Value);
        asset.SetFloat("EndTransitionEnd", EndTransitionEnd.Value);
    }
}
