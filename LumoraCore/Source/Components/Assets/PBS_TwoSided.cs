// Copyright (c) 2026 LUMORAVR LTD. All rights reserved.
// Licensed under the LumoraVR Source Available License. See LICENSE in the project root.

using Lumora.Core.Math;

namespace Lumora.Core.Assets;

// One PBS surface lit correctly from both sides: a back face is shaded as the side it is, its normal map read
// the other way up, rather than as the dark inside of the front. Foliage cards, cloth, thin shells. Blended
// with both sides showing it draws its back faces before its front ones, so the two never cross.
//
// Not the dual-sided materials, which give each side textures of their own. VertexColors puts the mesh's
// colours on the albedo, the emission or the reflectance. -xlinka
[ComponentCategory("Assets/Materials")]
public class PBS_TwoSided : PBS_SurfaceBase
{
    [Group("Albedo")]
    public readonly Sync<VertexColorTarget> VertexColors;

    // Where a vertex colour blends across its triangle; see VertexColorSpace.gdshaderinc.
    [Group("Albedo")]
    public readonly Sync<ColorProfile> VertexColorInterpolationSpace;

    protected override PBSSurfaceFeatures Features => VertexColors.Value switch
    {
        VertexColorTarget.Albedo => PBSSurfaceFeatures.VertexAlbedo,
        VertexColorTarget.Emissive => PBSSurfaceFeatures.VertexEmissive,
        VertexColorTarget.Reflectance => PBSSurfaceFeatures.VertexReflectance,
        _ => PBSSurfaceFeatures.None
    };

    protected override bool BlendsInTwoPasses => true;

    public PBS_TwoSided() : base(Assets.Culling.None, Assets.AlphaHandling.Opaque)
    {
        VertexColors = new Sync<VertexColorTarget>(this, VertexColorTarget.None);
        VertexColorInterpolationSpace = new Sync<ColorProfile>(this, ColorProfile.Linear);
    }

    protected override void UpdateEffect(MaterialAsset asset)
    {
        asset.SetInt("VertexColorInterpolationSpace", (int)VertexColorInterpolationSpace.Value);
    }
}
